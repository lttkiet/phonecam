
import urllib.request, json, time

req = urllib.request.Request('http://127.0.0.1:18765/settings', data=json.dumps({'quality': 60}).encode('utf-8'), headers={'Content-Type': 'application/json'}, method='POST')
resp = urllib.request.urlopen(req)
print('Settings response:', resp.read().decode())

time.sleep(1)

t0 = time.time()
stream = urllib.request.urlopen('http://127.0.0.1:18765/stream.mjpg', timeout=5)
total = 0
frames = 0
while time.time() - t0 < 5.0:
    chunk = stream.read(65536)
    if not chunk: break
    total += len(chunk)
    frames += chunk.count(b'--frame')

dt = time.time() - t0
stream.close()
mbps = (total * 8.0) / 1e6 / dt
fps = frames / dt
print(f'Transferred: {total / (1024*1024):.2f} MB in {dt:.2f} s')
print(f'Bitrate: {mbps:.2f} Mbps')
print(f'Frame Rate: {fps:.2f} FPS ({frames} frames)')
