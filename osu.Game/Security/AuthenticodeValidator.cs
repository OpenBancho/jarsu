// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace osu.Game.Security
{
    public static class AuthenticodeValidator
    {
        // Designated OpenBancho / Jarsu Code Signing Certificate thumbprints
        public static readonly string[] AllowedThumbprints = new[]
        {
            "26C7B784BA0B1726D08299D8A630BE339CB5EBDE", // CN=Jarsu, O=OpenBancho (Active)
            "4407A1E7C55A677FF25197477729C98BF6B553DC"  // CN=Jarsu Security Code Signing (Backup)
        };

        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

        private const uint ERROR_SUCCESS = 0;
        private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>
        /// Validates that the file has a cryptographically intact Authenticode signature
        /// AND that the signer is specifically Jarsu / OpenBancho with our designated thumbprint.
        /// Rejects unsigned, tampered, or foreign-signed files.
        /// </summary>
        public static bool ValidateBinarySignature(string filePath, out string failureReason)
        {
            failureReason = string.Empty;

            if (!OperatingSystem.IsWindows())
            {
                // Non-Windows platforms bypass Authenticode verification
                return true;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                failureReason = $"Binary not found at '{filePath}'";
                return false;
            }

            // 1. Verify PKCS#7 / Authenticode signature digest integrity with WinVerifyTrust
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };

            IntPtr pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            uint trustResult;
            try
            {
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = 2, // WTD_UI_NONE
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1, // WTD_CHOICE_FILE
                    pFile = pFileInfo,
                    dwStateAction = 0,
                    hWVTStateData = IntPtr.Zero,
                    pwszURLReference = null,
                    dwProvFlags = 0x00000080, // WTD_CACHE_ONLY_URL_RETRIEVAL
                    dwUIContext = 0,
                    pSignatureSettings = IntPtr.Zero
                };

                trustResult = WinVerifyTrust(new IntPtr(-1), WINTRUST_ACTION_GENERIC_VERIFY_V2, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(pFileInfo);
            }

            // Trust result must be SUCCESS (0) or UNTRUSTEDROOT (0x800B0109).
            // Any other code means: no signature (0x800B0100), bad digest/tampered (0x80096010), etc.
            if (trustResult != ERROR_SUCCESS && trustResult != CERT_E_UNTRUSTEDROOT)
            {
                failureReason = $"Authenticode integrity check failed (WinVerifyTrust code: 0x{trustResult:X8}) - binary was tampered or unsigned";
                return false;
            }

            // 2. Extract and inspect signer certificate
            X509Certificate2 signerCert;
            try
            {
                using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
                signerCert = new X509Certificate2(rawCert);
            }
            catch (Exception ex)
            {
                failureReason = $"Failed to read embedded digital certificate: {ex.Message}";
                return false;
            }

            // 3. Strict Subject verification: must be Jarsu / OpenBancho
            string subject = signerCert.Subject ?? string.Empty;
            if (!subject.Contains("CN=Jarsu", StringComparison.OrdinalIgnoreCase) ||
                !subject.Contains("OpenBancho", StringComparison.OrdinalIgnoreCase))
            {
                failureReason = $"Foreign digital signature detected: '{subject}'. Only official OpenBancho / Jarsu builds are permitted.";
                return false;
            }

            // 4. Strict Thumbprint verification: must match our official certificate thumbprints
            string thumbprint = signerCert.Thumbprint ?? string.Empty;
            bool thumbprintMatched = false;
            foreach (var allowed in AllowedThumbprints)
            {
                if (string.Equals(thumbprint, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    thumbprintMatched = true;
                    break;
                }
            }

            if (!thumbprintMatched)
            {
                failureReason = $"Untrusted code signing thumbprint: '{thumbprint}'. Foreign or forged certificates are strictly rejected.";
                return false;
            }

            return true;
        }
    }
}
