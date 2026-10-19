
import urllib.request, time

t0 = time.time()
resp = urllib.request.urlopen('http://127.0.0.1:18765/stream.mjpg', timeout=5)
total = 0
frames = 0
while time.time() - t0 < 5.0:
    chunk = resp.read(65536)
    if not chunk: break
    total += len(chunk)
    frames += chunk.count(b'--frame')

dt = time.time() - t0
resp.close()
mbps = (total * 8.0) / 1e6 / dt
MBps = (total / (1024*1024)) / dt
fps = frames / dt
print(f'Transferred: {total / (1024*1024):.2f} MB in {dt:.2f} s')
print(f'Bitrate: {mbps:.2f} Mbps ({MBps:.2f} MB/s)')
print(f'Frames estimated: {frames} (~{fps:.1f} FPS)')
