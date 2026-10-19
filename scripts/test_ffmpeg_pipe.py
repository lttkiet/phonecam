import subprocess
import urllib.request
import time
import sys
import threading

url = "http://127.0.0.1:18765/stream.h264"
req = urllib.request.Request(url)
print("Connecting to", url)
resp = urllib.request.urlopen(req, timeout=10)

ffmpeg_cmd = [
    r".\artifacts\windows\ffmpeg.exe",
    "-nostats",
    "-hide_banner", "-loglevel", "error",
    "-hwaccel", "auto",
    "-fflags", "nobuffer", "-flags", "low_delay",
    "-probesize", "131072",
    "-f", "h264", "-i", "pipe:0",
    "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1"
]

proc = subprocess.Popen(ffmpeg_cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)

def feeder():
    try:
        while True:
            chunk = resp.read(65536)
            if not chunk: break
            proc.stdin.write(chunk)
            proc.stdin.flush()
    except Exception as e:
        pass
    finally:
        try: proc.stdin.close()
        except: pass

threading.Thread(target=feeder, daemon=True).start()
# Drain stderr continuously to prevent pipe buffer deadlock!
threading.Thread(target=proc.stderr.read, daemon=True).start()

frame_size = 1920 * 1080 * 4
frame_count = 0
start_time = time.time()
print(f"Reading frames of size {frame_size} bytes...")

buf = bytearray(frame_size)
while time.time() - start_time < 5.0:
    mv = memoryview(buf)
    offset = 0
    while offset < frame_size:
        n = proc.stdout.readinto(mv[offset:])
        if not n:
            print("stdout closed")
            sys.exit(1)
        offset += n
    frame_count += 1
    elapsed = time.time() - start_time
    print(f"[{elapsed:4.2f}s] Frame #{frame_count:3d} (FPS: {frame_count/elapsed:.1f})")

elapsed = time.time() - start_time
print(f"Total: {frame_count} frames in {elapsed:.2f}s -> {frame_count/elapsed:.1f} FPS")
proc.terminate()
