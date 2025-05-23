## Disclaimer

> [!WARNING]
> At this time this project is for educational content only. Interacting directly with the underlying UDK/sandbox services may or may not be supported/recommended by Microsoft. For now, this project is for experimentation only. Proceed with caution!

## Goal
I was interested in diving deep into the internals of Windows Sandbox (but not as deep as @alex-ilgayev in [this article](https://www.reddit.com/r/ReverseEngineering/comments/m2pgjj/windows_sandbox_technical_deep_dive/) :smile:) to learn how it all works. In particular, I was interested in seeing what could be done for persisting the sandbox's longer as well as how we could run multiple sandbox's side-by-side. Initially I discovered the new [wsb cli tool][4] and the GRPC server endpoint, new as of 24H2, and couldn't find much out there talking about the internals (only one [test project](https://github.com/smourier/WinformsSandbox) atm). So I figured it was time to explore and write up my own investigation journey.

Overall, I see a ton of use cases. For one, windows sandbox's could be an alternate target for extensions like VS Code's [Dev Containers](https://marketplace.visualstudio.com/items?itemName=ms-vscode-remote.remote-containers) for devs to deploy and debug their software. This would open up soo many doors for dev teams who couldn't even use containers before such as: teams who develop desktop applications, NET framework stacks, or teams who prefer not to deal with docker. I think that last one is particularly interesting since windows sandbox is installed w/o the need for a docker specific dependency (correct me if I'm wrong), albeit windows sandbox's of course are a windows only feature, so no cross-platform support (which is to be expected).

These sort of workflows would require a bit more robust control over sandbox's, such as persistence control and running multiple instances. A general management tool for exposing these experiences seemlessly would be ideal. [Dev Home](https://learn.microsoft.com/en-us/windows/dev-home/) would have been perfect if it weren't for the fact that it is being [discontinued](https://github.com/microsoft/devhome/pull/4028#issuecomment-2614526347) in May 2025 (how sad).

## Investigations
<details open>

<summary><b>Updates 4/14/25:</b></summary>

* Found minor mistake that was preventing us from interacting with our custom sandbox's created directly through UDK. Early disposal of `VMRunningReference` seems to pre-maturely shutdown VM in some capacity. After correcting that, we can now ping our custom sandbox, as well as target it with the `Invoke-Command`/`Enter-PSSession` PS commands. Additionally, even though we are circumventing the built-in sandbox management features and can't connect to it via that `wsb` cli (i.e. `wsb connect`), turns out we can pull in the `AxMSTSCLib` COM lib and reference the Remote Desktop ActiveX control within a windows forms project. This is demonstrated via [this repo](https://github.com/smourier/WinformsSandbox), albeit it relies on dynamically calling the built-in Windows Sandbox logic. I found that when creating the sandbox manually via UDK, I could manually set the pipe endpoint and user/password on the `_rdpClient` object which would work to load a RDP connection.
    * Note: While I could create multiple custom sandbox's, the Remote Desktop ActiveX control only seemed to allow us to connect to the first one. Haven't dug in far enough to explore the extent of the issue nor a workaround.
    * Note: Appears our custom windows sandbox are cleaned up after a period of time (5-10 mins?) IF we exit out of our custom program. I suspect this would be a non-issue if we stood up a service/GUI and stored those `VMRunningReference`s for the lifetime of the application.
* Ultimately this latest update confirms that we can startup _several_ of our own custom windows sandbox's, interact with them via a remote shell, but still only connect to the first one via RDP. Since we can execute any command we want on the sandbox, we could just as easily install VNC to fill that purpose.

</details>

<details open>

<summary><b>Updates 4/4/25:</b></summary>

* The below trick for running as admin/non-admin to get multiple sandbox's seems to be patched, as of `MicrosoftWindows.WindowsSandbox_0.5.0.0` (worked previously with `MicrosoftWindows.WindowsSandbox_0.4.31.0`). The `wsb` CLI command seems to now behave the same regardless of which elevation the process is running under (this was an expected change).
* Creating a sandbox directly using the UDK generated code (i.e. like what the test program in this repo is doing) seems to have some different behavior compared to before when I tested this. Previously I could ping the custom sandbox, however that no longer works and haven't dug into why yet. The rest of the code in the test project still functions, but still unable to interact with the VM in any of our prefered ways (via RDP or remote shell). I suspect there is some sort of process chain whitelist happening at some layer.
* For the built-in sandbox feature, found a way to interact with the VM directly w/o use of the IP or enabling PS remoting on the VM. Essentially we use the `Invoke-Command`/`Enter-PSSession` with the `-VMId` option. There's more to it for it work, so refer to sample script near bottom. I'm sure this was possible before, I just hadn't thought to try something like this. Mostly what I was curious was if I could use this technique to connect to my custom sandbox created when using the UDK libs directly, but unfortunately it did not work...

</details>

---

For my initial investigation details see my existing posts [here][1] and [here][2].

* ### Invetigate inner workings of [Windows Sandbox][0]
    * RDP wrapper UI client: `WindowsSandboxRemoteSession.exe`
    * Backend server component: `WindowsSandboxServer.exe`
        * Found GRPC endpoint behind named pipe `\\.\pipe\wsandbox\{USER_SESSION_GUID}`
            * Can use this to retrieve existing config options, such as password, IP, and other info
            * Can use existing `SandboxCommon.Grpc.GrpcClient` from `SandboxCommon.dll` to easily interact with the GRPC endpoint
        * VM management uses interop calls to various `WindowsUdk.Security.Isolation.*` objects (auto-generated by [CsWinRT][7] via `windowsudk.winmd`?). This appears to point to REG key(s) `HKLM\SOFTWARE\Microsoft\WindowsRuntime\ActivatableClassId\WindowsUdk.Security.Isolation.*` which point to `%SystemRoot%\system32\windowsudk.shellcommon.dll`.
        * Creating a VM will start several processes, the server plus:
            * `C:\Windows\System32\ManagedWindowsVM.exe`
            * `vmmemWindowsSandbox`
    * Reviewed new [wsb cli tool][4]
        * Interacts with GRPC backend
        * Of particular interest: `wsb list`, `wsb ip --id {ID}`, `wsb exec --id {ID} -c {CMD} -r {RUNAS}`
        * `wsb exec` only returns exit code, so not particular useful for output, but very powerful for configuring the sandbox w/o enabling any sort of reverse/remote shell. Escaping may be a little tricky, but PS [EncodedCommand][3] works perfecly here if need be.
        * Determined that we can start sandbox's and keep them alive indefinetely (until a host reboot at least). If we use `wsb start` followed by `wsb connect --id {ID}`, closing the UI window will no longer destroy the sandbox. We have to initiate a `wsb stop --id {ID}` to destory the sandbox.
    * See example PS script below for using the `SandboxCommon.Grpc.GrpcClient`, `wsb` cli tool, and PS remoting together
    * Investigating running multiple sandbox's
        * Discovered that starting an elevated and non-elevated shell will produce different `USER_SESSION_GUID`'s, therefore allowing us to run two sandbox's simulatenously (major caveat*)
        * Additionally, we can use a tool like [PsExec][5] w/ `-s` switch to run a shell as `NT AUTHORITY\SYSTEM` user, which can further create a third distinct sandbox (major caveat*)
        * Found `AreMultipleInstancesAllowed` boolean used by `WindowsSandboxServer.exe`, therefore there is built-in support for running multiple sandboxes under the current user session (w/o need for above tricks). However this bool seems to be feature locked. It will only return true if either of these conditions are met:
            * REG Key HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion has BuildBranch set to something that satisfies the regex ^(rs(?!.*release).+). In my case my current one is set to ge_release, so updating that value to something like rs_beta could work, although this is probably a bad idea as we don't really know what other software out there is relying on this value being set a certain way. Note that I never did actually attempt to modify my reg key to avoid any issues. I did however modify it within a sandbox instance just to make sure nothing bad would immediately happen, and while nothing actually did, I still wouldn't recommend it! Only do this if you're ok with things possibly going terribly wrong, and you can recover.
            * Result of [SLGetWindowsInformationDWORD][6] for product TerminalServices-RemoteConnectionManager-WVD-Enabled does not equal zero. The underlying code issues a PInvoke to SLC.dll to get the result into a out variable. While I'm not 100% certain what this is checking, what I suspect it is looking to see if a certain terminal services feature is licensed and is enabled. On my system, that value is zero, and I couldn't find an easy way to enable or modify it. This lead me down a rabbit hole that took me to a code project blog that shows how to list out this data, but I couldn't pursue it any further because code project seems to be [no more][8] and the wayback machine doesn't seem to archive the download files (see [here][9], plus my head was starting to hurt anyway 🤕).
* ### Investigate interfacing with UDK directly w/o reliance on any `WindowsSandbox*.dll` or `Sandbox*.dll` references
    * See `WindowsSandboxUDKTest` project for example setup
        * Most code mimicks similiar configuration applied by `WindowsSandboxServer`
    * The core mechanisms are controlled by the UDK library. Windows Sandbox wraps this into the `SandboxUDK.dll`. That library appears to be auto-generated, therefore we can interface with UDK by generating our own projection using [CsWinRT][7].
        * Main requirement is we must have the WINMD file that contains the `WindowsUdk.Security.Isolation.*` metadata: `windowsudk.winmd`
        * Just so happens `windowsudk.winmd` can be found in the `MicrosoftWindows.UndockedDevKit` system app directory, which can be determined with `Get-AppPackage -name "MicrosoftWindows.UndockedDevKit"` (check `WindowsSandboxUDKTest` folder for `10.0.26100`/`24H2` version).
        * `WindowsUdk.Security.Isolation.ManagedWindowsVM` is the star of the show (see `WindowsSandboxUDKTest` for implementation details)
    * Observations:
        * Since we are creating everything directly, the GRPC named pipe and `wsb` cli tool do not apply and cannot help us in any way (these are `WindowsSandboxServer` specific dependencies). However, this can benefit us greatly since we can choose to run and manage multiple sandbox's simultaneously.
        * While executing the program does actually create a sandbox which can be pinged and SYSTEM commands executed against it, I was unable to properly configure the default user. Executing the `CreateProcess(_vm, _runningReference, cmd)`  would throw system exceptions when targeting the `SpecifiedUser` of `WDAGUtilityAccount`. I suspect I'm missing something very simple (for another day...)
        * Command for enabling remote PS does seem to execute w/o error, but invoking a remote command still fails. Contrast that to the built-in Windows Sandbox's which I can enable and invoke remote PS no problem. Again, I suspect something simple is missing (also for another day...)
        * I have not specifically tried to RDP over any protocols. Using @smourier's [WinformsSandbox][10] project might help, although I believe it relies on the named pipe setup by `WindowsSandboxServer`, which doesn't exists when we run our own. So RDP TBD.
        * If we don't use the `ManagedWindowsVM.Terminate()` command, the sandbox will persist even if the `WindowsSandboxUDKTest` program exits. However, all `Isolation.ManagedWindowsVM` instances will be destroyed if either a) the host reboots, or b) the `ManagedWindowsVM.exe` process is killed.
        * We can interact with an existing sandbox (even ones created by `WindowsSandboxServer`) by using the VM ID (shown as the username of the process in task manager). This would even allow us to execute processes on the VM. So in theory if we persist the VM ID's in some sort of storage medium, we can interact with previously created sandbox's in between process restarts. One downside being that it doesn't seem possible to retrieve the original `VMOptions` (unless we store that too).

<sup>\* See initial invesitgation links for details on caveats</sup>

---

Script to start a new sandbox, enable PS remoting, retrieve the auto-generated IP & password, and finally invoke a remote cmd:

```ps1
# Get path from: Get-AppxPackage *WindowsSandbox*
using assembly "{path_to}\MicrosoftWindows.WindowsSandbox_0.5.0.0_x64__cw5n1h2txyewy\SandboxCommon.dll"

wsb start

[guid]$sandboxId = wsb list

$client = [SandboxCommon.Grpc.GrpcClient]::new()

$config = $client.GetRdpClientConfigAsync($sandboxId).Result

wsb exec --id $sandboxId -r SYSTEM -c 'powershell -c Enable-PSRemoting -force -SkipNetworkProfileCheck'

$sandboxIp = wsb ip --id $sandboxId

$cred = New-Object System.Management.Automation.PSCredential ("WDAGUtilityAccount", (ConvertTo-SecureString ($config.Password) -AsPlainText -Force))

Invoke-Command -ComputerName $sandboxIp -Credential $cred -ScriptBlock { Write-Host "Hello from sandbox!"; whoami; hostname; }
# OR # Enter-PSSession -ComputerName $sandboxIp -Credential $cred

wsb stop --id $sandboxId
```

---

Similiar script as above, but w/o using IP or enabling PS remoting:

```ps1
# Get path from: Get-AppxPackage *WindowsSandbox*
using assembly "{path_to}\MicrosoftWindows.WindowsSandbox_0.5.0.0_x64__cw5n1h2txyewy\SandboxCommon.dll"

wsb start

[guid]$sandboxId = wsb list

$client = [SandboxCommon.Grpc.GrpcClient]::new()

$config = $client.GetRdpClientConfigAsync($sandboxId).Result

$vmId = $config.VMId

$cred = New-Object System.Management.Automation.PSCredential ("WDAGUtilityAccount", (ConvertTo-SecureString ($config.Password) -AsPlainText -Force))

# It's possible to use `Invoke-Command`/`Enter-PSSession` directly w/o enabling remote PS for a sandbox. Requires
# overriding existing `Get-VM` cmdlet w/ VM details (which is what these commands rely on). There is probably
# a better way to do this, but the internal mechanisms of `Invoke-Command` seem locked down.
function Get-VM($Id) {
    # VMName doesn't seem to matter in this case
    @( [PSCustomObject]@{ VMName="WinSbx"; VMId=[guid]::Parse($id); State=2; } )
    # TODO: query `wsb list` and append to existing cmdlet results?
}

Invoke-Command -VMId $vmId -Credential $cred -ScriptBlock { Write-Host "Hello from sandbox!"; whoami; hostname; }
# OR # Enter-PSSession -VMId $vmId -Credential $cred

wsb stop --id $sandboxId
```

[0]: https://github.com/microsoft/Windows-Sandbox
[1]: https://github.com/smourier/WinformsSandbox/issues/1
[2]: https://github.com/TomasHubelbauer/ps-remoting/issues/1#issuecomment-2675440214
[3]: https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_powershell_exe?view=powershell-5.1#-encodedcommand-base64encodedcommand
[4]: https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli
[5]: https://learn.microsoft.com/en-us/sysinternals/downloads/psexec
[6]: https://learn.microsoft.com/en-us/windows/win32/api/slpublic/nf-slpublic-slgetwindowsinformationdword
[7]: https://github.com/microsoft/CsWinRT
[8]: https://www.reddit.com/r/cpp/comments/1g6y1l5/comment/lsmjve3/?utm_source=share&utm_medium=web3x&utm_name=web3xcss&utm_term=1&utm_content=share_button
[9]: https://web.archive.org/web/20241115191349/https://www.codeproject.com/Articles/1006264/Windows-Software-Licensing-Data-and-Information-in#expand
[10]: https://github.com/smourier/WinformsSandbox