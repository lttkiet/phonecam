
import subprocess, socket, time

# Set up port forward 19999 -> 19999
subprocess.run(['adb', 'forward', 'tcp:19999', 'tcp:19999'], check=True)

# Start netcat receiver on phone piping to /dev/null
p = subprocess.Popen(['adb', 'shell', 'nc -l -p 19999 > /dev/null'])
time.sleep(0.5)

# Connect from PC and send 100 MB of pure memory bytes (no disk I/O)
s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
s.connect(('127.0.0.1', 19999))

chunk = b'X' * 65536
total = 100 * 1024 * 1024 # 100 MB
sent = 0
t0 = time.time()
while sent < total:
    s.sendall(chunk)
    sent += len(chunk)
s.close()
p.wait(timeout=5)
dt = time.time() - t0

mbps = (total * 8.0) / 1e6 / dt
MBps = (total / (1024 * 1024)) / dt
print(f'Transferred: {total / (1024*1024):.1f} MB in {dt:.2f} s')
print(f'Pure ADB Forward Throughput (RAM-to-RAM): {mbps:.2f} Mbps ({MBps:.2f} MB/s)')
