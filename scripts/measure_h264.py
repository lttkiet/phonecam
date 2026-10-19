import urllib.request
import time

url = "http://127.0.0.1:18765/stream.h264"
req = urllib.request.Request(url)
print("Connecting to", url)
with urllib.request.urlopen(req, timeout=10) as resp:
    print("Content-Type:", resp.headers.get("Content-Type"))
    print("X-Width:", resp.headers.get("X-Width"))
    print("X-Height:", resp.headers.get("X-Height"))
    start = time.time()
    total_bytes = 0
    nal_count = 0
    buf = bytearray()
    last_print = start
    while time.time() - start < 6.0:
        chunk = resp.read(65536)
        if not chunk:
            break
        total_bytes += len(chunk)
        buf.extend(chunk)
        pos = 0
        while True:
            idx = buf.find(b"\x00\x00\x00\x01", pos)
            if idx == -1:
                break
            nal_count += 1
            pos = idx + 4
        buf = buf[pos:]
        now = time.time()
        if now - last_print >= 1.0:
            rate_mbps = (total_bytes * 8.0) / ((now - start) * 1_000_000.0)
            fps = nal_count / (now - start)
            print(f"[{now - start:4.1f}s] NALs: {nal_count:4d} | FPS: {fps:5.1f} | Bitrate: {rate_mbps:4.2f} Mbps")
            last_print = now

    elapsed = time.time() - start
    print(f"Summary: {elapsed:.2f}s, {total_bytes} bytes, {(total_bytes*8)/(elapsed*1e6):.2f} Mbps, {nal_count/elapsed:.1f} FPS")
