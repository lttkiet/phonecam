
import subprocess, socket, time

# Test phone -> PC speed (the camera streaming direction)
# Set up adb forward 19998 -> 19998
subprocess.run(['adb', 'forward', 'tcp:19998', 'tcp:19998'], check=True)

# On phone, run a process that generates 100MB from /dev/zero and sends through nc
p = subprocess.Popen(['adb', 'shell', 'toybox dd if=/dev/zero bs=64k count=1600 2>/dev/null | nc -l -p 19998'])
time.sleep(0.5)

s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.connect(('127.0.0.1', 19998))

total = 0
t0 = time.time()
while True:
    buf = s.recv(65536)
    if not buf: break
    total += len(buf)

s.close()
p.wait(timeout=5)
dt = time.time() - t0

mbps = (total * 8.0) / 1e6 / dt
MBps = (total / (1024 * 1024)) / dt
print(f'Phone -> PC Stream: Transferred {total / (1024*1024):.1f} MB in {dt:.2f} s')
print(f'Bitrate: {mbps:.2f} Mbps ({MBps:.2f} MB/s)')
