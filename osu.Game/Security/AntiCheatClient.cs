// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using osu.Framework.Logging;
using osu.Game.Online.API;
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

        private static MethodInfo? canSubmitScoreMethod;
        private static MethodInfo? submitScoreMethod;
        private static MethodInfo? getLoginTicketMethod;
        private static bool hasNotified;
        private static volatile bool loadFinished;
        private static FileStream? dllGuard;

        private enum ServerMode
        {
            Unknown,
            Strict,
            NonStrict,
        }

        // The game server decides whether the anti-cheat is used at all (CLIENT_MODE of the server).
        private static volatile ServerMode serverMode = ServerMode.Unknown;
        private static readonly HttpClient mode_client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        private static INotificationOverlay? notificationOverlay;

        private static IAPIProvider? api;
        public static void CheckAndNotify(INotificationOverlay notifications, IAPIProvider? apiProvider = null)
        {
            if (apiProvider != null)
                api = apiProvider;

            notificationOverlay = notifications;
            if (hasNotified) return;
            hasNotified = true;

            if (!IsStrictBuild)
            {
                // Non-strict build: only notes that a module is present and ignores it.
                loadAndNotify(notifications);
                loadFinished = true;
                return;
            }

            // Strict build: ask the server first. On a nonstrict server the module is not even loaded,
            // so there is no handshake, no checks and no contact with the anti-cheat server.
            string? apiUrl = api?.Endpoints.APIUrl;

            Task.Run(() =>
            {
                try
                {
                    serverMode = apiUrl == null ? ServerMode.Unknown : resolveServerMode(apiUrl, 10);

                    if (serverMode == ServerMode.Strict)
                        loadAndNotify(notifications);
                    else if (serverMode == ServerMode.NonStrict)
                        Logger.Log("Server is nonstrict: the anti-cheat module is not loaded and not used.", LoggingTarget.Runtime, LogLevel.Important);
                    else
                        Logger.Log("Could not determine the server's client mode.", LoggingTarget.Runtime, LogLevel.Error);
                }
                finally
                {
                    loadFinished = true;
                }
            });
        }

        /// <summary>
        /// Reads the server's client mode from its public API info route. A server that does not report one does not use the anti-cheat.
        /// </summary>
        private static ServerMode resolveServerMode(string apiUrl, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    string json = mode_client.GetStringAsync($"{apiUrl.TrimEnd('/')}/api/v2").GetAwaiter().GetResult();
                    string? mode = JObject.Parse(json).Value<string>("client_mode");

                    return string.Equals(mode, "strict", StringComparison.OrdinalIgnoreCase) ? ServerMode.Strict : ServerMode.NonStrict;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Server mode lookup failed (attempt {i + 1}): {ex.Message}", LoggingTarget.Runtime, LogLevel.Debug);
                }

                System.Threading.Thread.Sleep(3000);
            }

            return ServerMode.Unknown;
        }

        private static readonly System.Collections.Generic.List<string> pending_errors = new();
        private static readonly object load_lock = new();
        private static bool moduleLoadAttempted;
        private static Type? loadedModuleType;

        /// <summary>
        /// Loads and initialises the anti-cheat module before the game UI exists (used by the launch splash).
        /// Safe to call more than once; failures are kept and shown as notifications once the UI is up.
        /// </summary>
        /// <param name="progress">Receives short human-readable status lines.</param>
        /// <returns>Whether the module is loaded and running.</returns>
        public static bool Preload(Action<string>? progress = null)
        {
            lock (load_lock)
            {
                loadModule(progress);
                return IsAntiCheatLoaded;
            }
        }

        private static void fail(string message)
        {
            LastSecurityError = message;
            pending_errors.Add(message);
            Logger.Log($"[AntiCheat Security Alert] {message}", LoggingTarget.Runtime, LogLevel.Error);
        }

        private static void loadModule(Action<string>? progress)
        {
            if (moduleLoadAttempted)
                return;

            moduleLoadAttempted = true;

            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dll_name);
            bool dllExists = File.Exists(dllPath);

#if STRICT_CLIENT
            // 1. Verify host game executable signature
            progress?.Invoke("Verifying game files...");
            string? hostExe = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(hostExe) && File.Exists(hostExe))
            {
                string hostName = Path.GetFileName(hostExe);
                bool isDevRunner = hostName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(hostName, "dotnet.exe", StringComparison.OrdinalIgnoreCase);

                if (!isDevRunner && !AuthenticodeValidator.ValidateBinarySignature(hostExe, out string hostSigErr))
                    fail($"Game executable signature rejected: {hostSigErr}");
            }

            // 2. Verify anticheat.dll signature before loading
            if (!dllExists)
            {
                fail("STRICT client requires the anti-cheat module (anticheat.dll). Please place it in the game directory.");
                return;
            }

            progress?.Invoke("Verifying anti-cheat module...");

            // Keep the file open for reading (nobody can write to it or swap it) from the signature check until the
            // process ends, so the file that was verified is the file that gets loaded.
            try
            {
                dllGuard = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                fail($"anticheat.dll could not be locked for verification: {ex.Message}");
                return;
            }

            if (!AuthenticodeValidator.ValidateBinarySignature(dllPath, out string dllSigErr))
            {
                fail($"anticheat.dll signature rejected: {dllSigErr}");
                return;
            }

            try
            {
                progress?.Invoke("Starting OpenWatcher...");

                var assembly = Assembly.LoadFrom(dllPath);
                var moduleType = assembly.GetType("osu.AntiCheat.AntiCheatModule");
                var initMethod = moduleType?.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static);
                bool? result = initMethod?.Invoke(null, null) as bool?;

                if (result == true)
                {
                    IsAntiCheatLoaded = true;
                    loadedModuleType = moduleType;

                    canSubmitScoreMethod = moduleType?.GetMethod("CanSubmitScore", BindingFlags.Public | BindingFlags.Static);
                    submitScoreMethod = moduleType?.GetMethod("SubmitScore", BindingFlags.Public | BindingFlags.Static);
                    getLoginTicketMethod = moduleType?.GetMethod("GetLoginTicket", BindingFlags.Public | BindingFlags.Static);

                    Logger.Log("Anti-cheat module loaded and signature verified successfully.", LoggingTarget.Runtime, LogLevel.Debug);
                }
                else
                {
                    fail("Anti-cheat module initialization failed.");
                }
            }
            catch (Exception ex)
            {
                fail($"Failed to load anti-cheat module: {ex.Message}");
            }
#else
            // NON-STRICT client
            if (dllExists)
                Logger.Log("anticheat.dll detected in non-strict client; ignoring.", LoggingTarget.Runtime, LogLevel.Debug);
#endif
        }

        private static void loadAndNotify(INotificationOverlay notifications)
        {
            lock (load_lock)
            {
                // No-op when the launch splash already loaded the module.
                loadModule(null);

                foreach (string error in pending_errors)
                    notifications.Post(new SimpleErrorNotification { Text = IsStrictBuild ? $"[Security Violation]: {error}" : error });

                pending_errors.Clear();

                if (IsAntiCheatLoaded && loadedModuleType != null)
                {
                    // The module talks to its own backend; the host only lets it identify the current player.
                    if (api != null)
                    {
                        var provider = api;
                        loadedModuleType.GetMethod("ConfigureServerContext", BindingFlags.Public | BindingFlags.Static)
                                        ?.Invoke(null, new object[] { new Func<string?>(() => provider.AccessToken) });
                    }

                    var regMethod = loadedModuleType.GetMethod("RegisterViolationCallback", BindingFlags.Public | BindingFlags.Static);
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
            }

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

        /// <summary>
        /// Anti-cheat handshake for the login: the module obtains a ticket from the anti-cheat server which the game
        /// server requires from this (strict) client. Returns null when there is no working anti-cheat module.
        /// </summary>
        public static string? GetLoginTicket(string? username)
        {
            if (!IsStrictBuild)
                return null;

            // The API may start logging in before the game has tried to load the module; wait for that attempt.
            for (int i = 0; i < 300 && !loadFinished; i++)
                System.Threading.Thread.Sleep(100);

            if (serverMode != ServerMode.Strict || !IsAntiCheatLoaded || getLoginTicketMethod == null)
                return null;

            try
            {
                return getLoginTicketMethod.Invoke(null, new object?[] { username }) as string;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AntiCheatClient] Login handshake failed: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return null;
            }
        }

        /// <summary>
        /// Hands the score being submitted over to the anti-cheat module, which sends it to its own server.
        /// </summary>
        public static void ReportScore(long tokenId, int beatmapId, string payload)
        {
            if (!IsStrictBuild || serverMode != ServerMode.Strict || !IsAntiCheatLoaded || submitScoreMethod == null)
                return;

            try
            {
                submitScoreMethod.Invoke(null, new object[] { tokenId, beatmapId, payload });
            }
            catch (Exception ex)
            {
                Logger.Log($"[AntiCheatClient] Score hand-over failed: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        /// <summary>
        /// Asks the anti-cheat module whether a score may be submitted. Returns null when allowed, otherwise the reason.
        /// </summary>
        public static string? GetScoreSubmissionBlockReason()
        {
            // A nonstrict server does not use the anti-cheat: nothing gates the score.
            if (!IsStrictBuild || serverMode == ServerMode.NonStrict)
                return null;

            if (!IsAntiCheatLoaded || canSubmitScoreMethod == null)
                return LastSecurityError ?? "Anti-cheat module is not loaded or signature verification failed.";

            try
            {
                return canSubmitScoreMethod.Invoke(null, null) as string;
            }
            catch (Exception ex)
            {
                Logger.Log($"[AntiCheatClient] Score gate failed: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return ex.Message;
            }
        }
    }
}
