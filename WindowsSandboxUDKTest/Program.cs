// Interact with existing sandbox's not created by this current process (id = username of process)
foreach (var exVmId in new [] { "" })
{
    try {
        if (!Guid.TryParse(exVmId ,out var exVmGuid)) continue;

        var exVm = WindowsUdk.Security.Isolation.ManagedWindowsVM.Open(exVmGuid);

        Console.WriteLine($"Existing VM: {exVm.Id} / Storage Id: {exVm.Storage.Id}");

        exVm.TerminatedReasonChanged += (sender, args) => {
            Console.WriteLine("Existing VM terminated");
        };

        using var exRunningRef = exVm.CreateRunningReference();

        var vmNet = exVm.GetNetworkInformation(exRunningRef);

        Console.WriteLine($"Existing VM Networks: {vmNet.Mode} / {vmNet.Interfaces.Count}");

        foreach (var net in vmNet.Interfaces) {
            Console.WriteLine("\t{0}: {1}", net.Id, string.Join(',', net.IPAddresses));
        }

        // exVm.Terminate();
    }
    catch (Exception ex) {
        Console.WriteLine($"Couldn't open existing VM: {ex.Message}");
    }
}

var _vmOptions = new WindowsUdk.Security.Isolation.VMOptions() {
    DisplayName = "CustomWindowsSandbox",
    // DisplayName = "WindowsSandbox",
    NetworkingMode = WindowsUdk.Security.Isolation.VMNetworkingMode.Nat,
    MaxMemoryMB = 4096U
};

_vmOptions.HyperVSocketServiceConfigurations.Add(new (new Guid("A715AC94-B745-4889-9A0F-772D85A3CFA4"))); // NLMServiceId
_vmOptions.HyperVSocketServiceConfigurations.Add(new (new Guid("F58797F6-C9F3-4D63-9BD4-E52AC020E586"))); // LSMServiceId

var _vm = new WindowsUdk.Security.Isolation.ManagedWindowsVM(_vmOptions);

Console.WriteLine($"\nCreating VM: {_vm.Id} / Default User: {_vm.DefaultUserName}");

try {
    var _runningReference = _vm.CreateRunningReference();

    Console.WriteLine($"VM Created: {_vm.Id} / Storage Id: {_vm.Storage.Id}");

    _vm.TerminatedReasonChanged += (sender, args) => {
        Console.WriteLine("VM Terminated");
    };

    var vmNet = _vm.GetNetworkInformation(_runningReference);

    Console.WriteLine($"VM Networks: {vmNet.Mode} / {vmNet.Interfaces.Count}");

    foreach (var net in vmNet.Interfaces) {
        Console.WriteLine("\t{0}: {1}", net.Id, string.Join(',', net.IPAddresses));
    }

    #region Activate default user

    var user = _vm.DefaultUserName;
    var pass = Guid.NewGuid();
    // var pass = 123456;

    Console.WriteLine(pass);

    CreateProcess(_vm, _runningReference, $"net user {user} {pass}", true);
    CreateProcess(_vm, _runningReference, $"net user {user} /active:yes", true);
    CreateProcess(_vm, _runningReference, $"wcsetupagent.exe AddUserToUsersGroup {user}", true);
    CreateProcess(_vm, _runningReference, $"wcsetupagent.exe AddUserToAdminGroup {user}", true);

    #endregion

    #region Specialization Commands

    var VMSpecializationCommands = new [] {
        "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Network\\NewNetworkWindowOff\" /f",
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FlyoutMenuSettings\" /v ShowLockOption /t REG_DWORD /d 0 /f",
        "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v ShowTaskViewButton /t REG_DWORD /d 0 /f"
    };

    foreach(var c in VMSpecializationCommands)
    {
        CreateProcess(_vm, _runningReference, c, true);
    }

    #endregion

    // Configure RDP
    CreateProcess(_vm, _runningReference, "reg add \"HKLM\\System\\CurrentControlSet\\Control\\Terminal Server\" /v b8ba549e-fb39-441c-b71e-44e9e4818210 /t REG_DWORD /d 1 /f", true);

    CreateProcess(_vm, _runningReference, $"wcsetupagent.exe NotifyRestoreComplete", true);

    Thread.Sleep(1000);

    // var cmd = @"powershell -c exit((whoami).Length)";
    var cmd = @"powershell -c Enable-PSRemoting -force -SkipNetworkProfileCheck"; // Not working?

    CreateProcess(_vm, _runningReference, cmd, true);

    // CreateProcess(_vm, _runningReference, cmd); // Not working?
}
catch (Exception ex) {
    Console.WriteLine(ex);
}
finally {
    _vm.Terminate();
}

Thread.Sleep(1000);

var r = _vm.TerminatedReason;

Console.WriteLine("Terminated Reason: {0}", r);

Console.WriteLine("Done");


static uint CreateProcess(WindowsUdk.Security.Isolation.ManagedWindowsVM vm, WindowsUdk.Security.Isolation.VMRunningReference vmRunningRef, string cmd, bool runAsSystem = false)
{
    var opts = new WindowsUdk.Security.Isolation.VMCreateProcessOptions() {
        // RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.StandardUser // ContainerUser (Default)
        // RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.System, // NT AUTHORITY\SYSTEM
        // RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.Administrator, // ContainerAdministrator
        // RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.SpecifiedUser, // Not working?

        // UseExistingLoginSession = true,
        // UserName = _vm.DefaultUserName,
        // WorkingDirectory = @"C:\"
    };

    if (runAsSystem) {
        opts.RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.System;
    } else {
        opts.RunAs = WindowsUdk.Security.Isolation.VMCreateProcessRunAs.SpecifiedUser;
        opts.UserName = vm.DefaultUserName;
        opts.UseExistingLoginSession = true;
    }

    var createProcessResult = vm.CreateProcessInVM(vmRunningRef, cmd, opts);

    if (createProcessResult.Status != WindowsUdk.Security.Isolation.VMCreateProcessStatus.Success)
        throw createProcessResult.ExtendedError;

    createProcessResult.Process.WaitForExit();

    if (!createProcessResult.Process.TryGetExitCode(out var exitCode))
        throw new InvalidOperationException("Unable to query the process result.");

    Console.WriteLine($"Process Exit Code: {exitCode}");

    return exitCode;
}