# Lesson: Ring Buffers (Circular Buffers) for Real-Time Systems

This is standalone learning material you can paste into ChatGPT Study Mode. It explains **what ring buffers are**, why they’re fast, how they work at a low level, and how to choose/design them for different workloads.

## 1) What is a ring buffer?
A **ring buffer** (circular buffer) is a fixed-size buffer that behaves like a queue. When you reach the end of the buffer, you “wrap around” to the beginning.

Think of it as a conveyor belt with two pointers:
- **head** (write position / producer)
- **tail** (read position / consumer)

The ring is typically **bounded** (fixed capacity), which makes it ideal for real-time systems where you want predictable memory usage.

## 2) Why ring buffers are used in high-performance systems
- **O(1)** enqueue/dequeue operations
- **No allocations** on the hot path (if you preallocate)
- Great cache locality (data is contiguous in memory)
- Natural fit for streaming data: audio, sensors, telemetry
- Very good for **SPSC** (single producer + single consumer) lock-free designs

## 3) The core math (the “wrap” trick)
### A) Using wrapped indices
You can store `head` and `tail` as indices `0..capacity-1` and wrap manually:
- `head = (head + 1) % capacity`
- `tail = (tail + 1) % capacity`

This is simple, but `%` can be slower and you must handle full/empty carefully.

### B) Using monotonic counters (preferred for lock-free designs)
Store `head` and `tail` as **ever-increasing counters** (e.g., `long`), and compute the address via masking:

```
offset = counter % capacity
```

If capacity is a power of two, you can replace modulo with a fast mask:

```
offset = counter & (capacity - 1)
```

This is why many high-performance ring buffers require **capacity to be a power of two**.

## 4) Full vs empty (the classic ambiguity)
If you store only wrapped indices, `head == tail` can mean:
- empty (no data)
- full (producer wrapped around to tail)

Common solutions:
- **Keep one slot empty**: capacity usable is `capacity-1`.
- **Store a count**: extra shared variable (can be tricky in lock-free).
- **Use monotonic counters**: `used = head - tail` tells you exactly how full it is.

In monotonic-counter rings:
- **empty** when `head == tail`
- **full** when `(head - tail) == capacity`

## 5) Ring buffer types by concurrency model
### A) SPSC (Single Producer, Single Consumer)
Best performance and simplest correctness.

Why it’s easier:
- Only producer writes `head`
- Only consumer writes `tail`
- Each side mostly reads the other’s counter

SPSC is ideal for:
- audio capture → processing thread
- sensor ingest → processing pipeline
- IPC between 2 processes (one writer, one reader)

### B) MPSC / SPMC (Many-to-one / one-to-many)
Harder because multiple writers (or readers) contend for the same counter.
Typical techniques:
- atomic fetch-add to reserve slots
- per-slot sequence numbers or per-slot ownership markers

### C) MPMC (Many Producer, Many Consumer)
Hardest, highest overhead.
Most robust approach:
- **per-slot sequence numbers** (Dmitry Vyukov-style bounded queue patterns)

Takeaway:
- If you can redesign your pipeline into multiple SPSC stages, do that first.

## 6) What “lock-free” means here (vs wait-free)
- **Lock-free**: system makes progress overall (some thread will complete) without mutexes.
- **Wait-free**: every thread completes in a bounded number of steps.

Most practical ring buffers aim for lock-free (or even “mostly lock-free”) because it’s simpler and faster.

## 7) Memory ordering (why it matters)
### The problem
The producer must ensure the consumer never sees a partially written element.

Example bug without ordering:
1. Producer stores `head = head + 1` (signals “new item exists”)
2. Consumer reads head and tries to read the item
3. Producer hasn’t finished writing the item data yet → consumer reads garbage/torn data

### The common fix (acquire/release)
For a fixed-size SPSC ring (slots):
- Producer:
  - write slot data
  - **release store** to `head`
- Consumer:
  - **acquire load** of `head`
  - read slot data
  - **release store** to `tail`

In .NET/C#, this is commonly implemented with:
- `Volatile.Read`
- `Volatile.Write`

## 8) Fixed-size vs variable-size ring buffers
### A) Fixed-size slots (simplest, fastest)
Each entry is the same size (e.g., a struct or fixed byte array).

- **Pros**: simple indexing, no fragmentation, no wrap complexity
- **Cons**: wastes space if messages vary in size

Use for:
- audio frames (fixed samples per frame)
- sensor samples with fixed schema

### B) Variable-size messages in a byte ring
Store records as:

```
[recordHeader][payloadBytes...][padding]
```

Now you must handle:
- records that don’t fit at the end (wrap handling)
- detecting “in-progress” partial writes

Common patterns:
- **wrap marker**: special header value that tells the reader “jump to offset 0”
- **two-phase commit**: mark header as incomplete, write payload, then flip a committed flag/length

This is the pattern used in `NanoLink`’s shared-memory sensor ring.

## 9) Buffer sizing and latency (how to think about capacity)
Capacity is a tradeoff:
- bigger buffer → fewer drops under bursts, but can allow higher latency if consumer lags
- smaller buffer → lower worst-case backlog, but more drops under bursts

Rule of thumb (simple mental model):
- Let peak burst rate = \(R_p\) msgs/sec
- Consumer rate = \(R_c\) msgs/sec
- Burst duration = \(T\) seconds
- Needed extra capacity (msgs) ≈ \((R_p - R_c) * T\)

For byte rings, include message size distribution (p95/p99 sizes).

## 10) Common pitfalls (practical)
- Using unbounded queues in real-time paths (latency explodes)
- Putting producer-written and consumer-written variables on the same cache line (false sharing)
- Measuring performance with frequent `Console.WriteLine` (dominates latency)
- Per-message allocations (GC spikes)
- Mixing multiple producers into an SPSC design (corruption)
- Variable-size rings without a proper wrap strategy (reader gets stuck/corrupt data)

## 11) Mapping this lesson to the NanoLink system
In NanoLink’s implementation:
- The ring stores **variable-size records** in a **byte ring**.
- Wrap is handled using a **wrap marker**.
- Partial writes are prevented via **two-phase commit** (negative length → payload → positive length).
- Producer and consumer synchronize using `Volatile.Read/Write` on shared counters.

## 12) Study Mode prompts (copy/paste)
- “Explain why a power-of-two capacity makes ring buffers faster.”
- “Prove that `used = head - tail` is safe in SPSC if head/tail are monotonic counters.”
- “Walk through a wrap marker scenario in a variable-size byte ring.”
- “What exact bug happens if producer publishes `head` before writing payload?”
- “How would you redesign an MPMC system into multiple SPSC stages?”
- “Compare ring buffers vs linked-list queues for cache and latency behavior.”


