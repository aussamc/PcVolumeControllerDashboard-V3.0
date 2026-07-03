using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using PcVolumeControllerDashboard.Core.Audio;

namespace PcVolumeControllerDashboard.Platform.Windows;

/// <summary>
/// Audio backend that routes volume/mute operations through the VoiceMeeter
/// Remote API (VoicemeeterRemote64.dll). The DLL is loaded dynamically from the
/// VoiceMeeter install directory, located via the Windows registry.
///
/// Windows-only; selected only when the user picks the VoiceMeeter backend.
/// Ported from the WPF host's <c>VoiceMeeterService</c> to implement the neutral
/// <see cref="IAudioBackend"/> (returns <see cref="AudioTarget"/> DTOs and adds
/// the relative-adjust / toggle primitives).
///
/// Target key format:
///   VM_STRIP:N  — Hardware/virtual input strip N (0-based)
///   VM_BUS:N    — Output bus N (0-based)
///
/// Volume scale: 0 % = −60 dB, 100 % = +12 dB (72 dB total range).
/// </summary>
public sealed class VoiceMeeterBackend : IAudioBackend
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string DllName = "VoicemeeterRemote64.dll";
    private const string RegKeyInstallDir = @"SOFTWARE\VB-Audio\VoiceMeeter";
    private const string RegKeyUninstall  = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Strip and bus counts indexed by VoiceMeeter type (1=Vanilla, 2=Banana, 3=Potato).
    private static readonly int[] StripCounts = { 0, 3, 5, 8 };
    private static readonly int[] BusCounts   = { 0, 2, 5, 8 };

    private const float DbMin = -60f;
    private const float DbMax = +12f;
    private const float DbRange = DbMax - DbMin; // 72 f

    // How often to poll IsParametersDirty() to detect VM going offline/online.
    private const int AvailabilityPollMs = 2000;

    // ── Native delegates ─────────────────────────────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VmrLoginDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VmrLogoutDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VmrGetVoicemeeterTypeDelegate(out int pType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VmrIsParametersDirtyDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int VmrGetParameterFloatDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string paramName,
        out float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int VmrSetParameterFloatDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string paramName,
        float value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int VmrGetParameterStringADelegate(
        [MarshalAs(UnmanagedType.LPStr)] string paramName,
        [MarshalAs(UnmanagedType.LPStr, SizeConst = 512)] StringBuilder output);

    // ── Fields ────────────────────────────────────────────────────────────────

    private IntPtr _libHandle = IntPtr.Zero;
    private bool _disposed;
    private bool _loggedIn;

    private VmrLoginDelegate?              _login;
    private VmrLogoutDelegate?             _logout;
    private VmrGetVoicemeeterTypeDelegate? _getType;
    private VmrIsParametersDirtyDelegate?  _isDirty;
    private VmrGetParameterFloatDelegate?  _getFloat;
    private VmrSetParameterFloatDelegate?  _setFloat;
    private VmrGetParameterStringADelegate? _getStringA;

    private int  _vmType;    // 1=Vanilla, 2=Banana, 3=Potato; 0=unknown
    private bool _isAvailable;
    private bool _dllFound;

    private System.Threading.Timer? _pollTimer;

    private readonly Action<string> _log;

    // ── IAudioBackend ─────────────────────────────────────────────────────────

    public string BackendName => "VoiceMeeter";

    public bool IsAvailable => _isAvailable;

    public event Action? AvailabilityChanged;

    public event Action? TargetsChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <param name="logger">
    /// Optional delegate for diagnostic messages; may be invoked on any thread.
    /// </param>
    public VoiceMeeterBackend(Action<string>? logger = null)
    {
        _log = logger ?? (_ => { });
    }

    // ── Initialise ────────────────────────────────────────────────────────────

    public void Initialise()
    {
        _dllFound = TryLoadDll();

        if (!_dllFound)
        {
            _log("VoiceMeeterBackend: DLL not found. VoiceMeeter is not installed.");
            SetAvailability(false);
            return;
        }

        TryLogin();

        _pollTimer = new System.Threading.Timer(PollAvailability, null,
            AvailabilityPollMs, AvailabilityPollMs);
    }

    public void InvalidateCache() { /* VoiceMeeter targets are derived live from _vmType; nothing cached. */ }

    // ── TryLoadDll ────────────────────────────────────────────────────────────

    private bool TryLoadDll()
    {
        string? dllPath = ResolveInstallDir();
        if (dllPath == null)
        {
            _log("VoiceMeeterBackend: Could not locate install directory in registry.");
            return false;
        }

        try
        {
            _libHandle = NativeLibrary.Load(dllPath);
        }
        catch (Exception ex)
        {
            _log($"VoiceMeeterBackend: Failed to load '{dllPath}': {ex.Message}");
            return false;
        }

        try
        {
            _login      = GetDelegate<VmrLoginDelegate>(_libHandle, "VBVMR_Login");
            _logout     = GetDelegate<VmrLogoutDelegate>(_libHandle, "VBVMR_Logout");
            _getType    = GetDelegate<VmrGetVoicemeeterTypeDelegate>(_libHandle, "VBVMR_GetVoicemeeterType");
            _isDirty    = GetDelegate<VmrIsParametersDirtyDelegate>(_libHandle, "VBVMR_IsParametersDirty");
            _getFloat   = GetDelegate<VmrGetParameterFloatDelegate>(_libHandle, "VBVMR_GetParameterFloat");
            _setFloat   = GetDelegate<VmrSetParameterFloatDelegate>(_libHandle, "VBVMR_SetParameterFloat");
            _getStringA = GetDelegate<VmrGetParameterStringADelegate>(_libHandle, "VBVMR_GetParameterStringA");
        }
        catch (Exception ex)
        {
            _log($"VoiceMeeterBackend: Failed to bind API functions: {ex.Message}");
            return false;
        }

        _log($"VoiceMeeterBackend: DLL loaded from '{dllPath}'.");
        return true;
    }

    private static T GetDelegate<T>(IntPtr lib, string name) where T : Delegate
    {
        IntPtr ptr = NativeLibrary.GetExport(lib, name);
        return Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    // ── Registry lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path to VoicemeeterRemote64.dll, or null if not found.
    /// Primary: HKLM\SOFTWARE\VB-Audio\VoiceMeeter @ InstallDir
    /// Fallback: scan HKLM Uninstall keys for VoiceMeeter display name.
    /// </summary>
    private static string? ResolveInstallDir()
    {
        // Primary
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegKeyInstallDir);
            string? dir = key?.GetValue("InstallDir") as string;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string candidate = Path.Combine(dir.TrimEnd('\\', '/'), DllName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { /* continue to fallback */ }

        // Fallback: scan uninstall keys
        try
        {
            using RegistryKey? uninstall = Registry.LocalMachine.OpenSubKey(RegKeyUninstall);
            if (uninstall != null)
            {
                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? sub = uninstall.OpenSubKey(subKeyName);
                    if (sub == null) continue;
                    string? displayName = sub.GetValue("DisplayName") as string;
                    if (displayName == null ||
                        !displayName.Contains("VoiceMeeter", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? location = sub.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(location))
                    {
                        string candidate = Path.Combine(location.TrimEnd('\\', '/'), DllName);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
        }
        catch { /* ignored */ }

        return null;
    }

    // ── Login / logout ────────────────────────────────────────────────────────

    private void TryLogin()
    {
        if (_login == null) return;
        try
        {
            int result = _login();
            // 0 = OK, 1 = OK (was already logged in), -1 = cannot connect to server, -2 = login error
            _loggedIn = result >= 0;
            if (_loggedIn)
            {
                RefreshVmType();
                SetAvailability(true);
                _log($"VoiceMeeterBackend: Logged in (type={_vmType}).");
            }
            else
            {
                SetAvailability(false);
                _log($"VoiceMeeterBackend: Login returned {result} — VoiceMeeter may not be running.");
            }
        }
        catch (Exception ex)
        {
            _loggedIn = false;
            SetAvailability(false);
            _log($"VoiceMeeterBackend: Login exception: {ex.Message}");
        }
    }

    private void TryLogout()
    {
        if (_logout == null || !_loggedIn) return;
        try { _logout(); } catch { }
        _loggedIn = false;
    }

    // ── Availability polling ──────────────────────────────────────────────────

    private void PollAvailability(object? _)
    {
        if (_disposed || _isDirty == null) return;
        try
        {
            int result = _isDirty();
            bool nowAvailable = result >= 0; // -2 means VM not running; -1 means not initialized

            if (!nowAvailable && _loggedIn)
            {
                TryLogout();
            }
            else if (nowAvailable && !_loggedIn)
            {
                TryLogin();
                return; // TryLogin calls SetAvailability itself.
            }
            else if (nowAvailable && _vmType == 0)
            {
                RefreshVmType();
            }

            SetAvailability(nowAvailable);
        }
        catch (Exception ex)
        {
            _log($"VoiceMeeterBackend: Poll error: {ex.Message}");
            SetAvailability(false);
        }
    }

    private void SetAvailability(bool available)
    {
        if (_isAvailable == available) return;
        _isAvailable = available;
        try { AvailabilityChanged?.Invoke(); } catch { }
        // Availability flips change which targets exist (none when offline).
        try { TargetsChanged?.Invoke(); } catch { }
    }

    // ── VM type detection ─────────────────────────────────────────────────────

    private void RefreshVmType()
    {
        if (_getType == null) return;
        try
        {
            int result = _getType(out int type);
            _vmType = result == 0 ? type : 0;
        }
        catch { _vmType = 0; }
    }

    // ── IAudioBackend: target enumeration ─────────────────────────────────────

    public IReadOnlyList<AudioTarget> GetAvailableTargets()
    {
        var list = new List<AudioTarget>();
        if (!_isAvailable || _vmType <= 0) return list;

        int strips = (_vmType < StripCounts.Length) ? StripCounts[_vmType] : 0;
        int buses  = (_vmType < BusCounts.Length)   ? BusCounts[_vmType]   : 0;

        for (int i = 0; i < strips; i++)
        {
            string key   = $"VM_STRIP:{i}";
            string label = GetStripOrBusLabel($"Strip[{i}].Label", $"Strip {i + 1}");
            list.Add(new AudioTarget
            {
                Key           = key,
                Label         = label,
                ProcessName   = "VoiceMeeter",
                IsVoiceMeeter = true,
                IsLive        = true,
                Volume        = NormalizedToPercent(GetVolumeByKey(key)),
                Muted         = GetMuteByKey(key) == true,
                State         = "Active",
            });
        }

        for (int i = 0; i < buses; i++)
        {
            string key   = $"VM_BUS:{i}";
            string label = GetStripOrBusLabel($"Bus[{i}].Label", $"Bus {i + 1}");
            list.Add(new AudioTarget
            {
                Key           = key,
                Label         = label,
                ProcessName   = "VoiceMeeter",
                IsVoiceMeeter = true,
                IsLive        = true,
                Volume        = NormalizedToPercent(GetVolumeByKey(key)),
                Muted         = GetMuteByKey(key) == true,
                State         = "Active",
            });
        }

        return list;
    }

    private string GetStripOrBusLabel(string vmParamName, string fallback)
    {
        if (_getStringA == null) return fallback;
        try
        {
            var sb = new StringBuilder(512);
            int result = _getStringA(vmParamName, sb);
            if (result == 0)
            {
                string label = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(label)) return label;
            }
        }
        catch { }
        return fallback;
    }

    private static int NormalizedToPercent(float normalized)
        => normalized < 0f ? 0 : (int)Math.Round(Math.Clamp(normalized, 0f, 1f) * 100f);

    // ── IAudioBackend: volume / mute ──────────────────────────────────────────

    public float GetVolumeByKey(string targetKey)
    {
        if (!_isAvailable || _getFloat == null) return -1f;
        string? paramName = KeyToGainParam(targetKey);
        if (paramName == null) return -1f;
        try
        {
            int result = _getFloat(paramName, out float db);
            return result == 0 ? DbToNormalized(db) : -1f;
        }
        catch { return -1f; }
    }

    // VoiceMeeter strips/buses are always-present mixer channels (no per-app
    // playing/idle notion), so an available key counts as active.
    public bool IsKeyActive(string targetKey) => GetVolumeByKey(targetKey) >= 0f;

    public bool SetVolumeByKey(string targetKey, float normalizedVolume)
    {
        if (!_isAvailable || _setFloat == null) return false;
        string? paramName = KeyToGainParam(targetKey);
        if (paramName == null) return false;
        float db = NormalizedToDb(Math.Clamp(normalizedVolume, 0f, 1f));
        try
        {
            int result = _setFloat(paramName, db);
            return result == 0;
        }
        catch { return false; }
    }

    public int AdjustVolumeByKey(string targetKey, int deltaPercent, int minPercent, int maxPercent)
    {
        float current = GetVolumeByKey(targetKey);
        if (current < 0f) return -1;

        int currentPct = (int)Math.Round(Math.Clamp(current, 0f, 1f) * 100f);
        int next = Math.Clamp(currentPct + deltaPercent, minPercent, maxPercent);
        return SetVolumeByKey(targetKey, next / 100f) ? next : -1;
    }

    public bool? GetMuteByKey(string targetKey)
    {
        if (!_isAvailable || _getFloat == null) return null;
        string? paramName = KeyToMuteParam(targetKey);
        if (paramName == null) return null;
        try
        {
            int result = _getFloat(paramName, out float value);
            return result == 0 ? value >= 0.5f : null;
        }
        catch { return null; }
    }

    public bool SetMuteByKey(string targetKey, bool mute)
    {
        if (!_isAvailable || _setFloat == null) return false;
        string? paramName = KeyToMuteParam(targetKey);
        if (paramName == null) return false;
        try
        {
            int result = _setFloat(paramName, mute ? 1f : 0f);
            return result == 0;
        }
        catch { return false; }
    }

    public bool? ToggleMuteByKey(string targetKey)
    {
        bool? current = GetMuteByKey(targetKey);
        bool next = !(current ?? false);
        return SetMuteByKey(targetKey, next) ? next : (bool?)null;
    }

    // ── Key → VoiceMeeter parameter name ─────────────────────────────────────

    private static string? KeyToGainParam(string key)
    {
        if (key.StartsWith("VM_STRIP:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(key[9..], out int n))
                return $"Strip[{n}].Gain";
        }
        else if (key.StartsWith("VM_BUS:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(key[7..], out int n))
                return $"Bus[{n}].Gain";
        }
        return null;
    }

    private static string? KeyToMuteParam(string key)
    {
        if (key.StartsWith("VM_STRIP:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(key[9..], out int n))
                return $"Strip[{n}].Mute";
        }
        else if (key.StartsWith("VM_BUS:", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(key[7..], out int n))
                return $"Bus[{n}].Mute";
        }
        return null;
    }

    // ── dB / normalised conversions ───────────────────────────────────────────

    // 0 % → −60 dB, 100 % → +12 dB (72 dB total range)
    private static float NormalizedToDb(float normalized) => DbMin + normalized * DbRange;
    private static float DbToNormalized(float db) => Math.Clamp((db - DbMin) / DbRange, 0f, 1f);

    // ── Utility: key detection (used by the host) ─────────────────────────────

    public static bool IsVoiceMeeterKey(string? key)
        => key != null &&
           (key.StartsWith("VM_STRIP:", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("VM_BUS:", StringComparison.OrdinalIgnoreCase));

    public static string MakeDisplayLabel(string key)
    {
        if (key.StartsWith("VM_STRIP:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[9..], out int s))
            return $"Strip {s + 1}";
        if (key.StartsWith("VM_BUS:", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(key[7..], out int b))
            return $"Bus {b + 1}";
        return key;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pollTimer?.Dispose();
        _pollTimer = null;

        TryLogout();

        if (_libHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libHandle);
            _libHandle = IntPtr.Zero;
        }
    }
}
