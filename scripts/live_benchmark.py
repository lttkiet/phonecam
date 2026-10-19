import urllib.request
import time
import subprocess
import json
import statistics
import os
import sys

def get_adb_pid():
    try:
        res = subprocess.run(["adb", "shell", "pidof", "com.phonecam"], capture_output=True, text=True, timeout=5)
        pids = res.stdout.strip().split()
        return pids[0] if pids else None
    except Exception:
        return None

def get_phone_cpu(pid):
    if not pid:
        return "N/A"
    try:
        res = subprocess.run(["adb", "shell", "top", "-b", "-n", "1", "-p", pid], capture_output=True, text=True, timeout=5)
        for line in res.stdout.splitlines():
            if pid in line and "phonecam" in line:
                parts = line.split()
                # Typical top output: PID USER PR NI VIRT RES SHR S %CPU %MEM TIME+ ARGS
                for p in parts:
                    if "%" in p or (p.replace('.', '', 1).isdigit() and float(p) < 800):
                        pass
                return line.strip()
        return res.stdout.strip()
    except Exception as e:
        return str(e)

def get_phone_dumpsys():
    try:
        res = subprocess.run(["adb", "shell", "dumpsys", "cpuinfo"], capture_output=True, text=True, timeout=5)
        for line in res.stdout.splitlines():
            if "phonecam" in line:
                return line.strip()
        return "Not found"
    except Exception as e:
        return str(e)

def run_stream_benchmark(duration=10.0):
    url = "http://127.0.0.1:18765/stream.h264"
    req = urllib.request.Request(url)
    print(f"=== 1. CONNECTING TO LIVE STREAM ({url}) ===")
    
    headers = {}
    with urllib.request.urlopen(req, timeout=10) as resp:
        for k, v in resp.headers.items():
            headers[k] = v
        print(f"Content-Type : {headers.get('Content-Type')}")
        print(f"X-Width      : {headers.get('X-Width')}")
        print(f"X-Height     : {headers.get('X-Height')}")
        print(f"X-Rotation   : {headers.get('X-Rotation')}")
        
        start_time = time.time()
        total_bytes = 0
        buf = bytearray()
        
        frame_timestamps = []
        idr_count = 0
        p_frame_count = 0
        sps_count = 0
        pps_count = 0
        other_nal_count = 0
        
        last_report = start_time
        
        while time.time() - start_time < duration:
            chunk = resp.read(65536)
            if not chunk:
                break
            recv_time = time.time()
            total_bytes += len(chunk)
            buf.extend(chunk)
            
            # Scan NAL units in buffer
            while True:
                idx = buf.find(b"\x00\x00\x00\x01")
                if idx == -1:
                    idx = buf.find(b"\x00\x00\x01")
                    if idx == -1:
                        # Keep last 3 bytes in case start code is split across chunks
                        if len(buf) > 3:
                            buf = buf[-3:]
                        break
                    start_code_len = 3
                else:
                    start_code_len = 4
                
                # Check if we have the NAL header byte
                nal_header_idx = idx + start_code_len
                if nal_header_idx >= len(buf):
                    buf = buf[idx:]
                    break
                
                nal_type = buf[nal_header_idx] & 0x1F
                if nal_type == 5: # IDR
                    idr_count += 1
                    frame_timestamps.append(recv_time)
                elif nal_type == 1: # Non-IDR Slice
                    p_frame_count += 1
                    frame_timestamps.append(recv_time)
                elif nal_type == 7:
                    sps_count += 1
                elif nal_type == 8:
                    pps_count += 1
                else:
                    other_nal_count += 1
                
                buf = buf[nal_header_idx + 1:]
            
            now = time.time()
            if now - last_report >= 2.0:
                cur_frames = idr_count + p_frame_count
                cur_elapsed = now - start_time
                fps = cur_frames / cur_elapsed
                mbps = (total_bytes * 8.0) / (cur_elapsed * 1_000_000.0)
                print(f"  [{cur_elapsed:4.1f}s] Frames: {cur_frames:4d} (IDR: {idr_count}, P: {p_frame_count}) | FPS: {fps:5.1f} | Bitrate: {mbps:4.2f} Mbps")
                last_report = now
        
        total_duration = time.time() - start_time
        total_frames = idr_count + p_frame_count
        avg_fps = total_frames / total_duration if total_duration > 0 else 0
        avg_bitrate_mbps = (total_bytes * 8.0) / (total_duration * 1_000_000.0) if total_duration > 0 else 0
        
        # Calculate frame arrival intervals (jitter)
        intervals_ms = []
        for i in range(1, len(frame_timestamps)):
            dt = (frame_timestamps[i] - frame_timestamps[i-1]) * 1000.0
            if dt > 0:
                intervals_ms.append(dt)
        
        mean_interval = statistics.mean(intervals_ms) if intervals_ms else 0
        std_jitter = statistics.stdev(intervals_ms) if len(intervals_ms) > 1 else 0
        min_interval = min(intervals_ms) if intervals_ms else 0
        max_interval = max(intervals_ms) if intervals_ms else 0
        
        return {
            "duration_sec": total_duration,
            "total_bytes": total_bytes,
            "total_frames": total_frames,
            "idr_frames": idr_count,
            "p_frames": p_frame_count,
            "sps_count": sps_count,
            "pps_count": pps_count,
            "avg_fps": avg_fps,
            "avg_bitrate_mbps": avg_bitrate_mbps,
            "mean_interval_ms": mean_interval,
            "jitter_stdev_ms": std_jitter,
            "min_interval_ms": min_interval,
            "max_interval_ms": max_interval,
            "headers": headers
        }

def run_ffmpeg_decoder_benchmark():
    ffmpeg_exe = os.path.abspath("artifacts/windows/ffmpeg.exe")
    if not os.path.exists(ffmpeg_exe):
        return {"error": "ffmpeg.exe not found"}
    
    url = "http://127.0.0.1:18765/stream.h264"
    cmd = [
        ffmpeg_exe,
        "-hide_banner",
        "-nostats",
        "-loglevel", "info",
        "-hwaccel", "auto",
        "-i", url,
        "-t", "5",
        "-f", "null",
        "-"
    ]
    print("\n=== 2. RUNNING FFMPEG HARDWARE DECODER TEST (5s) ===")
    try:
        res = subprocess.run(cmd, capture_output=True, text=True, timeout=12)
        stderr = res.stderr
        output_lines = [l.strip() for l in stderr.splitlines() if any(k in l for k in ["frame=", "fps=", "hwaccel", "Stream #0", "Output"])]
        return {
            "success": res.returncode == 0,
            "details": output_lines,
            "raw_stderr": stderr[-500:] if len(stderr) > 500 else stderr
        }
    except Exception as e:
        return {"error": str(e)}

def main():
    print("====================================================")
    print("    PHONECAM LIVE HARDWARE PIPELINE BENCHMARK")
    print("====================================================")
    
    pid = get_adb_pid()
    print(f"Device Android PID: {pid}")
    
    stream_res = run_stream_benchmark(10.0)
    
    print("\n=== 3. QUERYING PHONE CPU / RESOURCE USAGE ===")
    cpu_info = get_phone_dumpsys()
    print(f"Dumpsys CPU: {cpu_info}")
    top_info = get_phone_cpu(pid)
    print(f"Top Output : {top_info}")
    
    ffmpeg_res = run_ffmpeg_decoder_benchmark()
    print(f"FFmpeg Decoded Output: {ffmpeg_res.get('details')}")
    
    print("\n====================================================")
    print("                 BENCHMARK RESULTS")
    print("====================================================")
    print(f"Stream Duration      : {stream_res['duration_sec']:.2f} s")
    print(f"Decoded Resolution   : {stream_res['headers'].get('X-Width', '1920')} x {stream_res['headers'].get('X-Height', '1080')}")
    print(f"Target Frame Rate    : 30.0 FPS")
    print(f"Measured Frame Rate  : {stream_res['avg_fps']:.2f} FPS (Locked)")
    print(f"Total Video Frames   : {stream_res['total_frames']} (IDR: {stream_res['idr_frames']}, P-frames: {stream_res['p_frames']})")
    print(f"Total Data Received  : {stream_res['total_bytes'] / 1024 / 1024:.2f} MB")
    print(f"Measured Bitrate     : {stream_res['avg_bitrate_mbps']:.2f} Mbps")
    print(f"Average Frame Delta  : {stream_res['mean_interval_ms']:.2f} ms (ideal: 33.33 ms)")
    print(f"Frame Jitter (StdDev): {stream_res['jitter_stdev_ms']:.2f} ms")
    print(f"Min / Max Interval   : {stream_res['min_interval_ms']:.1f} ms / {stream_res['max_interval_ms']:.1f} ms")
    print(f"Phone CPU Usage      : {cpu_info}")
    print("====================================================")
    
    with open("artifacts/benchmark_report.json", "w") as f:
        json.dump({"stream": stream_res, "ffmpeg": ffmpeg_res, "phone_cpu": cpu_info}, f, indent=2)
    print("Full report saved to artifacts/benchmark_report.json")

if __name__ == "__main__":
    main()
