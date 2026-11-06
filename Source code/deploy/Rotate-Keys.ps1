# =====================================================================================
# Replace the live server's owner key and server key, in one command, from this PC.
#
#   powershell -ExecutionPolicy Bypass -File deploy\Rotate-Keys.ps1 -Server 154.65.110.136
#
# Written 2026-10-15, after a deploy's summary printed both keys and the log was pasted into a chat.
# Any key that has been copied somewhere it should not be is replaced, not trusted.
#
#  - OWNER KEY (/etc/rsp-owner-key): the first step into /owner. A new one signs every owner browser
#    out (the owner cookie's stamp is made from it); the authenticator app is NOT affected - its
#    secret is protected separately (OwnerTwoFactor). The new key is shown once, in THIS window only.
#  - SERVER KEY (/etc/rsp-server-key): how another server's control dashboard reaches this one.
#    Not shown; a dashboard that already watches this server must be given the new one
#    (sudo cat /etc/rsp-server-key on this server).
#
# Both live in the file AND in the service's override.conf (go-live.sh step 3); both are changed,
# the platform restarts (a few seconds), and if it does not answer again the old keys are put back.
# go-live.sh only makes a key when its file is missing, so later deploys keep the new ones.
#
#  -Server      the server's IP or hostname
#  -User        SSH user with sudo (default ubuntu)
#  -OnlyOwner   replace only the owner key
#  -OnlyServer  replace only the server key
# =====================================================================================
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = "ubuntu",
    [switch]$OnlyOwner,
    [switch]$OnlyServer
)
$ErrorActionPreference = "Stop"
if ($OnlyOwner -and $OnlyServer) { throw "Use -OnlyOwner or -OnlyServer, not both." }
$owner = if ($OnlyServer) { 'no' } else { 'yes' }
$serverKey = if ($OnlyOwner) { 'no' } else { 'yes' }
if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    throw "ssh is not on PATH. Install the Windows OpenSSH Client (Settings > Optional features)."
}

# The whole job runs on the server as root, in one SSH connection (one passphrase).
# Paths can be pointed elsewhere (RSP_TEST_ROOT) so the same text is tested on a PC with stand-ins.
$script = @'
set -e
R="${RSP_TEST_ROOT:-}"
SERVICE=remotesupport-platform
OVR="$R/etc/systemd/system/$SERVICE.service.d/override.conf"
OWNER_FILE="$R/etc/rsp-owner-key"
SERVER_FILE="$R/etc/rsp-server-key"
test -f "$OVR" || { echo "!! $OVR is missing - this server has not had go-live.sh. Nothing changed."; exit 1; }

newowner() { local k=""; until [ ${#k} -ge 40 ]; do k=$(head -c 48 /dev/urandom | base64 | tr -d '=+/\n' | cut -c1-40); done; printf '%s' "$k"; }
newserver() { head -c 32 /dev/urandom | od -An -tx1 | tr -dc '0-9a-f'; }

# Keep the old ones until the platform answers with the new ones.
cp -p "$OVR" "$OVR.before-rotation"
[ -f "$OWNER_FILE" ] && cp -p "$OWNER_FILE" "$OWNER_FILE.before-rotation"
[ -f "$SERVER_FILE" ] && cp -p "$SERVER_FILE" "$SERVER_FILE.before-rotation"
putback() {
  cp -p "$OVR.before-rotation" "$OVR"
  [ -f "$OWNER_FILE.before-rotation" ] && cp -p "$OWNER_FILE.before-rotation" "$OWNER_FILE"
  [ -f "$SERVER_FILE.before-rotation" ] && cp -p "$SERVER_FILE.before-rotation" "$SERVER_FILE"
  systemctl daemon-reload; systemctl restart "$SERVICE"
}

if [ "$ROTATE_OWNER" = yes ]; then
  OK=$(newowner)
  grep -q '^Environment=RSP_Platform__AdminKey=' "$OVR" || { echo "!! override.conf has no AdminKey line. Nothing changed."; exit 1; }
  sed -i "s|^Environment=RSP_Platform__AdminKey=.*|Environment=RSP_Platform__AdminKey=$OK|" "$OVR"
  (umask 077; printf '%s\n' "$OK" > "$OWNER_FILE"); chmod 600 "$OWNER_FILE"
  echo "   owner key: replaced"
fi
if [ "$ROTATE_SERVER" = yes ]; then
  SK=$(newserver)
  [ ${#SK} -eq 64 ] || { putback; echo "!! could not make a server key. Old keys kept."; exit 1; }
  if grep -q '^Environment=RSP_Platform__ServerKey=' "$OVR"; then
    sed -i "s|^Environment=RSP_Platform__ServerKey=.*|Environment=RSP_Platform__ServerKey=$SK|" "$OVR"
  else
    sed -i "/^Environment=RSP_Platform__AdminKey=/a Environment=RSP_Platform__ServerKey=$SK" "$OVR"
  fi
  (umask 077; printf '%s\n' "$SK" > "$SERVER_FILE"); chmod 600 "$SERVER_FILE"
  echo "   server key: replaced"
fi

echo "== Restarting the platform =="
systemctl daemon-reload
systemctl restart "$SERVICE"
code=000
for i in $(seq 1 60); do
  code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 3 http://127.0.0.1:8090/ || true)
  [ "$code" = 200 ] && break
  sleep 1
done
if [ "$code" != 200 ]; then
  echo "!! The platform did not answer after the restart (HTTP $code). Putting the old keys back."
  putback
  rm -f "$OVR.before-rotation" "$OWNER_FILE.before-rotation" "$SERVER_FILE.before-rotation"
  exit 1
fi
echo "   platform answers: HTTP 200"
rm -f "$OVR.before-rotation" "$OWNER_FILE.before-rotation" "$SERVER_FILE.before-rotation"

if [ "$ROTATE_OWNER" = yes ]; then
  echo ""
  echo "  NEW OWNER KEY - put it in your password manager now. Never paste it into a chat or an email:"
  echo ""
  echo "      $OK"
  echo ""
  echo "  Every owner browser is signed out; sign in again with this key and your authenticator code."
fi
if [ "$ROTATE_SERVER" = yes ]; then
  echo "  The new server key is NOT shown. When you add this server to another server's dashboard:"
  echo "      sudo cat $SERVER_FILE"
fi
'@

# Sent as base64: Windows PowerShell 5.1 mangles double quotes inside a native command's argument,
# and this script is full of them. Base64 is letters, digits, + / = - nothing a shell or PowerShell
# reads. The choices go at the top of the script itself, not through sudo's environment.
$full = "ROTATE_OWNER=$owner`nROTATE_SERVER=$serverKey`n" + $script.Replace("`r`n", "`n") + "`n"
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($full))
$remote = "echo $b64 | base64 -d | sudo bash"
if ($env:RSP_ROTATE_PRINT) { $remote; return }   # for testing: print the command instead of running it
Write-Host "== Replacing $(@(if ($owner -eq 'yes') {'the owner key'}; if ($serverKey -eq 'yes') {'the server key'}) -join ' and ') on $Server ==" -ForegroundColor Cyan
Write-Host "   (type your key's passphrase now)" -ForegroundColor Cyan
ssh "$User@$Server" $remote
if ($LASTEXITCODE -ne 0) { throw "the keys were not replaced - see the output above." }
Write-Host "`nDone." -ForegroundColor Green
