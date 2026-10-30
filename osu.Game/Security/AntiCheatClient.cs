// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Reflection;
using osu.Framework.Logging;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;

namespace osu.Game.Security
{
    public static class AntiCheatClient
    {
        private const string dll_name = "anticheat.dll";

        public static bool IsStrictBuild
        {
            get
            {
#if STRICT_CLIENT
                return true;
#else
                return false;
#endif
            }
        }

        public static bool IsAntiCheatLoaded { get; private set; }
        public static string? LastSecurityError { get; private set; }

        private static MethodInfo? secureAndEncryptScoreMethod;
        private static bool hasNotified;
        private static INotificationOverlay? notificationOverlay;

        public static void CheckAndNotify(INotificationOverlay notifications)
        {
            notificationOverlay = notifications;
            if (hasNotified) return;
            hasNotified = true;

            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dll_name);
            bool dllExists = File.Exists(dllPath);

#if STRICT_CLIENT
            // 1. Verify host game executable signature
            string? hostExe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(hostExe) && File.Exists(hostExe))
            {
                string hostName = Path.GetFileName(hostExe);
                bool isDevRunner = hostName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(hostName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);

                if (!isDevRunner && !AuthenticodeValidator.ValidateBinarySignature(hostExe, out string hostSigErr))
                {
                    LastSecurityError = $"Game executable signature rejected: {hostSigErr}";
                    Logger.Log($"[AntiCheat Security Alert] {LastSecurityError}", LoggingTarget.Runtime, LogLevel.Error);
                    notifications.Post(new SimpleErrorNotification
                    {
                        Text = $"[Security Violation]: Game executable signature rejected ({hostSigErr}). Scores will be blocked.",
                    });
                }
            }

            // 2. Verify anticheat.dll signature before loading
            if (dllExists)
            {
                if (!AuthenticodeValidator.ValidateBinarySignature(dllPath, out string dllSigErr))
                {
                    LastSecurityError = $"anticheat.dll signature rejected: {dllSigErr}";
                    Logger.Log($"[AntiCheat Security Alert] {LastSecurityError}", LoggingTarget.Runtime, LogLevel.Error);
                    notifications.Post(new SimpleErrorNotification
                    {
                        Text = $"[Security Violation]: anticheat.dll signature rejected ({dllSigErr}). Scores will be blocked.",
                    });
                    return;
                }

                try
                {
                    var assembly = Assembly.LoadFrom(dllPath);
                    var moduleType = assembly.GetType("osu.AntiCheat.AntiCheatModule");
                    var initMethod = moduleType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                    bool? result = initMethod?.Invoke(null, null) as bool?;

                    if (result == true)
                    {
                        IsAntiCheatLoaded = true;
                        secureAndEncryptScoreMethod = moduleType?.GetMethod("SecureAndEncryptScore", BindingFlags.Public | BindingFlags.Static);
                        Logger.Log("Anti-cheat module loaded and signature verified successfully.", LoggingTarget.Runtime, LogLevel.Debug);

                        var regMethod = moduleType?.GetMethod("RegisterViolationCallback", BindingFlags.Public | BindingFlags.Static);
                        regMethod?.Invoke(null, new object[] { new Action<string>(violation =>
                        {
                            LastSecurityError = violation;
                            Logger.Log($"[AntiCheat Alert]: {violation}", LoggingTarget.Runtime, LogLevel.Error);
                            notifications.Post(new SimpleErrorNotification
                            {
                                Text = $"[Security Violation]: {violation}",
                            });
                        })});
                    }
                    else
                    {
                        LastSecurityError = "Anti-cheat module initialization failed.";
                        Logger.Log(LastSecurityError, LoggingTarget.Runtime, LogLevel.Error);
                        notifications.Post(new SimpleErrorNotification
                        {
                            Text = LastSecurityError,
                        });
                    }
                }
                catch (Exception ex)
                {
                    LastSecurityError = $"Failed to load anti-cheat module: {ex.Message}";
                    Logger.Log(LastSecurityError, LoggingTarget.Runtime, LogLevel.Error);
                    notifications.Post(new SimpleErrorNotification
                    {
                        Text = LastSecurityError,
                    });
                }
            }
            else
            {
                LastSecurityError = "STRICT client requires the anti-cheat module (anticheat.dll). Please place it in the game directory.";
                Logger.Log("STRICT client started without anticheat.dll", LoggingTarget.Runtime, LogLevel.Important);
                notifications.Post(new SimpleErrorNotification
                {
                    Text = LastSecurityError,
                });
            }
#else
            // NON-STRICT client
            if (dllExists)
            {
                Logger.Log("anticheat.dll detected in non-strict client; ignoring.", LoggingTarget.Runtime, LogLevel.Debug);
            }
#endif

            // Check for optional test/audit payload (test-cheat.dll)
            string testCheatDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-cheat.dll");
            if (File.Exists(testCheatDllPath))
            {
                try
                {
                    var testAssembly = Assembly.LoadFrom(testCheatDllPath);
                    var testType = testAssembly.GetType("osu.AntiCheat.TestPayload.TestCheatPayload");
                    var initMethod = testType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                    initMethod?.Invoke(null, new object[] { new Action<string>(msg =>
                    {
                        Logger.Log($"[Test-Cheat Payload]: {msg}", LoggingTarget.Runtime, LogLevel.Debug);
                    })});
                }
                catch (Exception ex)
                {
                    Logger.Log($"Failed to initialize test-cheat.dll: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                }
            }
        }

        public static string? SecureAndEncryptScore(long scoreId, int beatmapId, int userId, string rawJson, string handshakeUrl, string? bearerToken)
        {
            if (!IsStrictBuild || !IsAntiCheatLoaded || secureAndEncryptScoreMethod == null)
            {
                Logger.Log($"[AntiCheatClient] SecureAndEncryptScore aborted: IsStrictBuild={IsStrictBuild}, IsAntiCheatLoaded={IsAntiCheatLoaded}, methodNull={secureAndEncryptScoreMethod == null}", LoggingTarget.Runtime, LogLevel.Error);
                return null;
            }

            try
            {
                Logger.Log($"[AntiCheatClient] Executing SecureAndEncryptScore: scoreId={scoreId}, map={beatmapId}, user={userId}, url={handshakeUrl}", LoggingTarget.Runtime, LogLevel.Debug);
                var result = secureAndEncryptScoreMethod.Invoke(null, new object?[] { scoreId, beatmapId, userId, rawJson, handshakeUrl, bearerToken }) as string;
                if (result == null)
                {
                    var lastErrProp = secureAndEncryptScoreMethod.DeclaringType?.GetProperty("LastError");
                    var lastErr = lastErrProp?.GetValue(null) as string;
                    LastSecurityError = lastErr ?? "Score signing rejected by anti-cheat";
                    Logger.Log($"[AntiCheatClient] SecureAndEncryptScore returned null! LastError: {LastSecurityError}", LoggingTarget.Runtime, LogLevel.Error);

                    notificationOverlay?.Post(new SimpleErrorNotification
                    {
                        Text = $"[Security Violation]: Score rejected: {LastSecurityError}",
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                LastSecurityError = ex.Message;
                Logger.Log($"[AntiCheatClient] Secure score encryption failed: {ex}", LoggingTarget.Runtime, LogLevel.Error);
                notificationOverlay?.Post(new SimpleErrorNotification
                {
                    Text = $"[Security Violation]: Score encryption failed: {ex.Message}",
                });
                return null;
            }
        }
    }
}
