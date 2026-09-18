param([string]$Name = 'drive1', [string]$GuestArgs = 'skip_op', [int]$DumpEvery = 30, [hashtable]$ExtraEnv = @{})
$repo = './SharpEmu-Source/sharpemu-gt7'
$capture = "$repo/artifacts/gt7/$Name"
$bin = "$repo/artifacts/bin/Release/net10.0/win-x64"
if (Get-Process SharpEmu -ErrorAction SilentlyContinue) { throw 'An emulator is already running.' }
New-Item -ItemType Directory -Force "$capture/images" | Out-Null
Get-ChildItem Env:SHARPEMU_* | Remove-Item
$env:SHARPEMU_LOG_VIDEOOUT_FPS = '1'
$env:SHARPEMU_LOG_PAD = '1'
$env:SHARPEMU_PERIODIC_SNAPSHOT_SECONDS = '30'
if ($GuestArgs) { $env:SHARPEMU_GUEST_ARGS = $GuestArgs }
$env:SHARPEMU_TRACE_GUEST_IMAGES = 'present'
$env:SHARPEMU_GUEST_IMAGE_DUMP_DIR = "$capture/images"
$env:SHARPEMU_SWAPCHAIN_DUMP_EVERY = "$DumpEvery"
# One diagnostic variable per run keeps a cause isolated; set after the reset above.
foreach ($k in $ExtraEnv.Keys) { Set-Item -Path "Env:$k" -Value ([string]$ExtraEnv[$k]) }
$launcher = Start-Process "$bin/SharpEmu.exe" -ArgumentList './GT7/PPSA01317-app/eboot.bin' -WorkingDirectory $bin -WindowStyle Hidden -PassThru -RedirectStandardOutput "$capture/out.log" -RedirectStandardError "$capture/err.log"
"launcher $($launcher.Id) capture $capture"