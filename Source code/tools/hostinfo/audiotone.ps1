# Plays a 1 kHz tone on the remote machine for a fixed number of seconds, and nothing else.
#
# WHY IT EXISTS. tools/audiocheck needs the customer's machine to be making a KNOWN sound while
# it records what arrives at the technician's end, so that the check is a pass or a fail rather
# than "I think I heard something". audioprobe.ps1 plays a tone too, but it plays two seconds
# buried in a full diagnostic; this does one thing at a length the harness chooses.
#
# WHY IT BUILDS THE WAVE THE WAY IT DOES. The first version ran a per-sample PowerShell loop --
# 44100 * seconds iterations of [Math]::Sin. For an eight-second tone that is 350,000 iterations
# and it took SEVERAL SECONDS to finish before a note was played. That does not merely make the
# harness slow: WASAPI loopback sends no packets at all when nothing is playing, so the silence
# while this script was still thinking arrived at the far end as MISSING AUDIO, and audiocheck
# read 4.08 s of sound in a 6 s window and called it a 32% clock error. The measurement was
# wrong, and it was wrong because of this loop.
#
# So: 48 kHz, which divides exactly by 1000 Hz, means one period is exactly 48 samples. Build
# those 48 samples once and copy the block. A whole minute of tone is then a few array copies.
#
# Test locally before sending it anywhere:
#   powershell -NoProfile -ExecutionPolicy Bypass -File audiotone.ps1
#
# The harness rewrites the marked line; the host runs scripts with -File and no arguments.
$Seconds = 6   # AUDIOTONE_SECONDS
$ErrorActionPreference = 'Continue'

$rate = 48000
$freq = 1000
$periodSamples = $rate / $freq          # exactly 48, which is the whole point of 48 kHz here
$totalSamples = [int]($rate * $Seconds)

$sw = [Diagnostics.Stopwatch]::StartNew()

# One period, as raw little-endian 16-bit bytes.
$period = New-Object byte[] ($periodSamples * 2)
for ($i = 0; $i -lt $periodSamples; $i++) {
    $v = [int](12000 * [Math]::Sin(2 * [Math]::PI * $i / $periodSamples))
    $b = [BitConverter]::GetBytes([int16]$v)
    $period[$i * 2] = $b[0]
    $period[$i * 2 + 1] = $b[1]
}

# Tile it. Block copies, not arithmetic -- this is the part that used to take seconds.
$dataBytes = $totalSamples * 2
$data = New-Object byte[] $dataBytes
$offset = 0
while ($offset -lt $dataBytes) {
    $take = [Math]::Min($period.Length, $dataBytes - $offset)
    [Array]::Copy($period, 0, $data, $offset, $take)
    $offset += $take
}

# Fade the first and last 20 ms so it starts and ends as a tone rather than a click. Only the
# edges are touched, so this stays cheap however long the tone is.
$fade = [int]($rate * 0.02)
for ($i = 0; $i -lt $fade; $i++) {
    foreach ($pair in @(@($i, ($i / $fade)), @(($totalSamples - 1 - $i), ($i / $fade)))) {
        $idx = [int]$pair[0]
        $gain = [double]$pair[1]
        $s = [BitConverter]::ToInt16($data, $idx * 2)
        $b = [BitConverter]::GetBytes([int16]([int]($s * $gain)))
        $data[$idx * 2] = $b[0]
        $data[$idx * 2 + 1] = $b[1]
    }
}

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([char[]]'RIFF'); $bw.Write([int](36 + $dataBytes))
$bw.Write([char[]]'WAVE'); $bw.Write([char[]]'fmt ')
$bw.Write([int]16); $bw.Write([int16]1); $bw.Write([int16]1)
$bw.Write([int]$rate); $bw.Write([int]($rate * 2))
$bw.Write([int16]2); $bw.Write([int16]16)
$bw.Write([char[]]'data'); $bw.Write([int]$dataBytes)
$bw.Write($data)
$bw.Flush()
[void]$ms.Seek(0, 'Begin')

# Say how long the building took. If this ever creeps up again, the harness's own numbers will
# go strange first and this line is what explains them.
Write-Output ("built " + $Seconds + " s of 1 kHz tone in " + [int]$sw.ElapsedMilliseconds + " ms")
Write-Output "playing..."
try {
    $player = New-Object System.Media.SoundPlayer($ms)
    $player.PlaySync()
    Write-Output "tone finished."
} catch {
    Write-Output ("the tone FAILED: " + $_.Exception.GetType().Name + ": " + $_.Exception.Message)
}
