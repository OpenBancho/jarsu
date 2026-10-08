// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using osu.Framework.Platform;

namespace osu.Game.Online.API
{
    public static class ClientTelemetry
    {
        public static string? GpuRenderer { get; set; }
        public static string? DisplayResolution { get; set; }
        public static int DisplayRefreshRate { get; set; }

        public static void Initialize(GameHost host)
        {
            try
            {
                if (host.Renderer != null)
                    GpuRenderer = host.Renderer.ToString();

                if (host.Window?.CurrentDisplayMode.Value is DisplayMode mode)
                {
                    DisplayResolution = $"{mode.Size.Width}x{mode.Size.Height}";
                    DisplayRefreshRate = (int)Math.Round(mode.RefreshRate);
                }
            }
            catch
            {
                // Non-critical
            }
        }

        public static string GetTelemetryJson()
        {
            try
            {
                var dict = new Dictionary<string, object>();

                dict["os"] = RuntimeInformation.OSDescription;
                dict["version"] = typeof(OsuGameBase).Assembly.GetName().Version?.ToString() ?? "unknown";
                dict["arch"] = RuntimeInformation.ProcessArchitecture.ToString();
                dict["cpu"] = getCpuName();
                dict["cores"] = Environment.ProcessorCount;
                dict["ram_mb"] = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
                dict["timezone"] = TimeZoneInfo.Local.Id;
                dict["locale"] = CultureInfo.CurrentUICulture.Name;

                string gpu = getGpuName();
                if (!string.IsNullOrEmpty(gpu))
                    dict["gpu"] = gpu;
                else if (!string.IsNullOrEmpty(GpuRenderer))
                    dict["gpu"] = GpuRenderer;

                if (!string.IsNullOrEmpty(DisplayResolution))
                    dict["resolution"] = DisplayResolution;
                if (DisplayRefreshRate > 0)
                    dict["refresh_rate"] = DisplayRefreshRate;

                // Hardware components for hashing
                string machineGuid = getMachineGuid();
                string diskSerial = getDiskInfo();
                string macAddress = getMacAddress();

                string combinedHw = $"{machineGuid}|{diskSerial}|{macAddress}|{dict["cpu"]}|{dict["cores"]}";
                dict["raw_hardware_hash"] = computeSha256(combinedHw);

                return JsonConvert.SerializeObject(dict);
            }
            catch
            {
                return "{}";
            }
        }

        private static string getCpuName()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                    if (key != null)
                    {
                        var val = key.GetValue("ProcessorNameString");
                        if (val != null)
                            return val.ToString()!.Trim();
                    }
                }
                catch
                {
                    // Fall through
                }
            }

            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Unknown CPU";
        }

        private static string getGpuName()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        string subKey = $@"SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{i:D4}";
                        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                        if (key != null)
                        {
                            var desc = key.GetValue("DriverDesc")?.ToString();
                            if (!string.IsNullOrEmpty(desc) && !desc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                                return desc.Trim();
                        }
                    }
                }
                catch
                {
                    // Fall through
                }
            }

            return GpuRenderer ?? string.Empty;
        }

        private static string getMachineGuid()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    if (key != null)
                    {
                        var val = key.GetValue("MachineGuid");
                        if (val != null)
                            return val.ToString()!;
                    }
                }
                catch
                {
                    // Fall through
                }
            }

            return Environment.MachineName;
        }

        private static string getDiskInfo()
        {
            try
            {
                var drives = DriveInfo.GetDrives();
                foreach (var d in drives)
                {
                    if (d.IsReady && d.DriveType == DriveType.Fixed)
                    {
                        return $"{d.Name}_{d.TotalSize}";
                    }
                }
            }
            catch
            {
                // Fall through
            }

            return "default_disk";
        }

        private static string getMacAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus == OperationalStatus.Up &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    {
                        var address = nic.GetPhysicalAddress().ToString();
                        if (!string.IsNullOrEmpty(address))
                            return address;
                    }
                }
            }
            catch
            {
                // Fall through
            }

            return "default_nic";
        }

        private static string computeSha256(string raw)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
