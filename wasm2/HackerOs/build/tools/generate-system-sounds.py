#!/usr/bin/env python3
"""Synthesizes the built-in HackerOS sound themes (ADR 0204) as WAV files.

Usage, from wasm2/HackerOs:
    python3 build/tools/generate-system-sounds.py /tmp/sounds
    for f in /tmp/sounds/*/*.wav; do
        ffmpeg -y -i "$f" -c:a libvorbis -q:a 2 -ar 22050 -ac 1 "${f%.wav}.oga"
    done
then copy /tmp/sounds/<theme>/*.oga to Platform/HackerOs.Platform.Core/Sounds/Themes/<theme>/.
Every sound is computed here (no sample from elsewhere), so the files carry no third-party rights.
"""
import math, struct, sys, wave, os

RATE = 22050
out = sys.argv[1]

def square(f, t):
    # Band-limited-ish square: first odd harmonics, softer than a pure square.
    return sum(math.sin(2 * math.pi * f * k * t) / k for k in (1, 3, 5, 7)) * 0.8

def saw(f, t):
    return sum(math.sin(2 * math.pi * f * k * t) / k * (-1) ** (k + 1) for k in range(1, 8)) * 0.6

def sine(f, t):
    return math.sin(2 * math.pi * f * t) + 0.25 * math.sin(2 * math.pi * f * 2.01 * t) * math.exp(-t * 6)

def note(wave_fn, freq, dur, attack=0.004, decay=None, vol=1.0, glide=None):
    n = int(RATE * dur)
    samples = []
    for i in range(n):
        t = i / RATE
        f = freq if glide is None else freq + (glide - freq) * (t / dur)
        env = min(1.0, t / attack)
        if decay:
            env *= math.exp(-t * decay)
        else:
            env *= min(1.0, (dur - t) / 0.012)
        samples.append(wave_fn(f, t) * env * vol)
    return samples

def seq(*parts, gap=0.0):
    result = []
    for p in parts:
        result += p + [0.0] * int(RATE * gap)
    return result

def mix(*tracks):
    n = max(len(t) for t in tracks)
    return [sum(t[i] for t in tracks if i < len(t)) for i in range(n)]

def offset(track, seconds):
    return [0.0] * int(RATE * seconds) + track

def write(name, samples):
    peak = max(abs(s) for s in samples) or 1
    tail = [0.0] * int(RATE * 0.05)
    data = b''.join(struct.pack('<h', int(s / peak * 0.85 * 32767)) for s in samples + tail)
    with wave.open(os.path.join(out, name + '.wav'), 'wb') as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(RATE); w.writeframes(data)

os.makedirs(os.path.join(out, 'hackeros'), exist_ok=True)
os.makedirs(os.path.join(out, 'soft'), exist_ok=True)

# "HackerOS": short console bleeps.
write('hackeros/message-new-instant', seq(note(square, 880, 0.06), note(square, 1320, 0.09), gap=0.02))
write('hackeros/dialog-warning', seq(note(square, 660, 0.08), note(square, 660, 0.08), gap=0.05))
write('hackeros/dialog-error', note(saw, 240, 0.32, glide=150))
write('hackeros/desktop-login', seq(*[note(square, f, 0.07) for f in (440, 554, 659, 880)], gap=0.01))
write('hackeros/desktop-logout', seq(*[note(square, f, 0.07) for f in (880, 659, 554, 440)], gap=0.01))

# "Soft": decaying sine chimes.
def chime(f, d=0.9, vol=1.0):
    return note(sine, f, d, attack=0.006, decay=5.5, vol=vol)

write('soft/message-new-instant', mix(chime(1046.5), offset(chime(1318.5), 0.09)))
write('soft/dialog-warning', mix(chime(784, 0.7), offset(chime(784, 0.7), 0.16)))
write('soft/dialog-error', mix(chime(392, 1.0), chime(370, 1.0, 0.7), offset(chime(294, 1.0), 0.12)))
write('soft/desktop-login', mix(*[offset(chime(f, 1.0), i * 0.11) for i, f in enumerate((523.25, 659.25, 783.99, 1046.5))]))
write('soft/desktop-logout', mix(*[offset(chime(f, 1.0), i * 0.11) for i, f in enumerate((1046.5, 783.99, 659.25, 523.25))]))
