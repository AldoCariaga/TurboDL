using Microsoft.Win32;

namespace TurboDL.Installer;

/// <summary>
/// The two registry "Software" keys Setup writes under: the user's (HKCU\Software: browser hosts, Run) and the
/// machine's (HKLM\Software: the Installed apps entry). <see cref="ForTest"/> puts both under one scratch key in HKCU.
/// </summary>
public sealed class RegistryLocations
{
    private readonly RegistryKey _userHive;
    private readonly string _userSoftware;
    private readonly RegistryKey _machineHive;
    private readonly string _machineSoftware;

    private RegistryLocations(RegistryKey userHive, string userSoftware, RegistryKey machineHive, string machineSoftware)
    {
        _userHive = userHive;
        _userSoftware = userSoftware;
        _machineHive = machineHive;
        _machineSoftware = machineSoftware;
    }

    public static RegistryLocations Real { get; } = new(Registry.CurrentUser, "Software", Registry.LocalMachine, "Software");

    /// <summary>
    /// HKCU\Software\TurboDL.SetupTest\<paramref name="name"/>\HKCU and ...\HKLM stand in for the real keys.
    /// </summary>
    public static RegistryLocations ForTest(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(['\\', '/']) >= 0) throw new ArgumentException("A plain key name, please.", nameof(name));
        var root = $@"{TestKeyParent}\{name}";
        return new(Registry.CurrentUser, root + @"\HKCU", Registry.CurrentUser, root + @"\HKLM");
    }

    /// <summary>Where <see cref="ForTest"/> keys live, under HKCU.</summary>
    public const string TestKeyParent = @"Software\TurboDL.SetupTest";

    /// <summary>A description for messages, e.g. HKEY_CURRENT_USER\Software\Google\...</summary>
    public string DescribeUser(string subPath) => $@"{_userHive.Name}\{_userSoftware}\{subPath}";

    public string DescribeMachine(string subPath) => $@"{_machineHive.Name}\{_machineSoftware}\{subPath}";

    public RegistryKey? OpenUser(string subPath, bool writable = false) => _userHive.OpenSubKey($@"{_userSoftware}\{subPath}", writable);

    public RegistryKey CreateUser(string subPath) => _userHive.CreateSubKey($@"{_userSoftware}\{subPath}");

    public void DeleteUser(string subPath) => _userHive.DeleteSubKeyTree($@"{_userSoftware}\{subPath}", throwOnMissingSubKey: false);

    public RegistryKey? OpenMachine(string subPath, bool writable = false) => _machineHive.OpenSubKey($@"{_machineSoftware}\{subPath}", writable);

    public RegistryKey CreateMachine(string subPath) => _machineHive.CreateSubKey($@"{_machineSoftware}\{subPath}");

    public void DeleteMachine(string subPath) => _machineHive.DeleteSubKeyTree($@"{_machineSoftware}\{subPath}", throwOnMissingSubKey: false);
}
