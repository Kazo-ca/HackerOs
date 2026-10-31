HackerOS Server
===============

The optional HackerOS server: the network relay (HTTP, TCP, UDP) for the HackerOS apps, accounts,
devices and file synchronization. It also serves HackerOS itself. Nothing is exposed to the
internet: by default it only answers on this computer (http://localhost:5888).

Install (French: doc/fr/wasm/dev/installable-server.md)
  https://github.com/Kazo-ca/HackerOs/blob/main/doc/en/wasm/dev/installable-server.md

  Windows (PowerShell as administrator):  .\install.ps1            or  .\install.ps1 -Lan 192.168.1.20
  Linux, macOS:                            sudo ./install.sh        or  sudo ./install.sh --lan 192.168.1.20

The installer prints a one-time pairing code. In HackerOS: Settings > Server connection >
Local server, type the server address and the code.

Another pairing code:  HackerOs.Server pair admin   (from the install folder, see the guide)
Remove:                .\uninstall.ps1  or  sudo ./uninstall.sh   (--purge / -Purge also deletes the data)

These programs are not signed: Windows SmartScreen asks to confirm ("More info", "Run anyway"),
and the macOS installer removes the download quarantine and signs the program ad hoc.
