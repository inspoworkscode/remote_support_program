# Test plan: this laptop as the server

Server = **LAPTOP-C3U7E7S6** (`192.168.1.115`, Wi-Fi, router `192.168.1.1`).
The branding is already set to `ws://192.168.1.115:8080` and the installers in `dist/installers/` are
built against it, so Phase 1 needs no build step.

> Note: the laptop got its IP from DHCP. If it ever changes, either reserve `192.168.1.115` for the laptop
> in the router, or update `branding/brand.json` and rebuild (`build.ps1 -Installers`).

---

## Phase 1 — internal test (office network)

### On this laptop (server + technician)

1. **Allow the port through Windows Firewall.** Open PowerShell **as Administrator** and run:
   ```powershell
   New-NetFirewallRule -DisplayName "Remote Support 8080" -Direction Inbound -Protocol TCP -LocalPort 8080 -Action Allow
   ```
2. **Start the server.** In a normal PowerShell window:
   ```powershell
   cd "C:\Users\Clint\Desktop\Remote Support\dist\server"
   .\RemoteSupport.Server.exe add-tech clint "YourPassword" "Clint"
   .\RemoteSupport.Server.exe run
   ```
   Leave this window open. You should see `Now listening on: http://0.0.0.0:8080`.
   (Add a second technician the same way if you want to test two techs on one call.)
3. **Check it's alive.** In a browser on the laptop open `http://localhost:8080/health` — you get `{"ok":true}`.
4. **Install the technician app.** Run `dist\installers\Your Company-RemoteSupport-Technician-Setup.exe`,
   launch it, sign in with the account from step 2. The console opens and shows "● connected".
5. **Stop the laptop sleeping** while you test (Settings → System → Power → Screen and sleep → Never).

### On a second office PC (the "customer")

6. **Check it can reach the server.** In a browser open `http://192.168.1.115:8080/health`. If that fails,
   the firewall rule from step 1 didn't apply or the PC is on a different network/VLAN.
7. **Install the client app.** Copy `Your Company-RemoteSupport-Client-Setup.exe` over and run it.
   Tick **"Enable unattended access"** if you want to test unattended too. The tray app starts.
8. **Request support.** Open the tray app, click **Request Support**, enter a name, click Request Now.

### What to verify

- Laptop: the technician console shows the call ringing (highlighted row + sound). Click **Accept Call**.
- Customer PC: the branded **"Allow a remote support connection?"** prompt appears. Click **Allow**.
- Laptop: a session window opens showing the customer's screen. Try:
  - moving the mouse / typing on the customer PC from the session window,
  - switching monitors in the dropdown (if the PC has more than one),
  - **Chat** tab: send a message; it appears in the customer's tray app,
  - **Files** tab: type `C:\Users` and Go, download a file, and **Send file…** one back,
  - **Scripts** tab: run `Get-ComputerInfo | Select-Object OsName, CsName` and watch the output stream,
  - **Leave / End**: the customer sees "session has ended".
- Chat before a call: on the customer PC type in the tray app's chat; it shows in the console's chat panel
  for every technician.
- Unattended (if ticked in step 7): with no call ringing, double-click the PC in the **Devices** list
  → it connects straight away (no prompt, because `unattendedRequireConsent` is off).
- Two technicians: sign in as a second tech on any PC; both see the ring; after one accepts, the other
  can double-click the session in **Active Sessions** to join the same screen.

---

## Phase 2 — external test (from outside the office)

The customer will connect from the internet, so the laptop needs a public address.

### Find your public address
9. On the laptop open `https://www.whatismyip.com` and note the **IPv4** address (e.g. `41.x.x.x`).
   If it changes often, set up a free dynamic-DNS name (No-IP / DuckDNS) and use that name instead.

### Forward the port on the office router
10. Log in to the router at `http://192.168.1.1`. Find **Port Forwarding** (sometimes "Virtual Server"
    or "NAT"). Add a rule:
    - External port: **8080**, Protocol: **TCP**
    - Internal IP: **192.168.1.115**, Internal port: **8080**
    Save. (Reserve the laptop's IP in the router's DHCP settings at the same time so this rule keeps working.)
11. From a phone **on mobile data (Wi-Fi off)** open `http://<public-ip>:8080/health`. If you get
    `{"ok":true}` the port is open. If not, the router blocks it or your ISP uses CGNAT — see "If port
    forwarding won't work" below.

### Rebuild the client for the public address
12. Edit `branding\brand.json` and set `"serverUrl": "ws://<public-ip>:8080"` (or your DDNS name).
13. Rebuild: `powershell -ExecutionPolicy Bypass -File build.ps1 -Installers`
14. Send the new **Client** installer to the external tester. They install it and click **Request Support**.
    The technician app on the laptop can keep using the LAN address, or reinstall it from the new build —
    both reach the same server.

### What to verify
- Same checklist as Phase 1, from the outside machine. Screen updates will be a little slower because the
  laptop's **upload** bandwidth is now the limit.

### If port forwarding won't work (ISP CGNAT, no router access)
Use a tunnel instead — no router changes. Example with ngrok (free):
```powershell
ngrok http 8080
```
It prints an address like `https://abc123.ngrok-free.app`. Set `serverUrl` to
`wss://abc123.ngrok-free.app` (note **wss**, the tunnel provides TLS), rebuild, and test. The address
changes each time ngrok restarts on the free plan, so this is for testing only.

---

## Security note for the test
Phase 1 and the port-forward route in Phase 2 use plain `ws://` — **unencrypted**. Fine for a test on your
own network; before real customers use it, put TLS in front (see DEPLOYMENT.md) and switch to `wss://`.
