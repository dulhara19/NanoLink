# Hot-swapping AI models at runtime (zero downtime): learning notes

## 1) What “hot-swapping a model” really means

In a real-time AI system, **inference requests are continuously arriving** (frames in a game, sensor ticks in robotics, streaming analytics). “Hot-swapping” means you can **replace the currently-serving model** (weights + execution graph + pre/post-processing configuration) **while requests keep flowing**, without:

- restarting the inference process
- dropping in-flight requests
- violating latency guarantees (especially tail latency like p99/p999)

The hard part is not “loading a new file” — it’s **safe concurrent access**, **resource management**, and **predictable performance** during the transition.

## 2) Goals → concrete requirements

### Primary goals as system requirements

- **Zero downtime**: inference API remains available throughout updates.
- **Per-request consistency**: each request must run **entirely** on one model version (no half-old/half-new state).
- **Low-latency reads**: inference threads should not block on heavy locks or I/O.
- **Safe reclamation**: old model resources must not be freed while any request still uses them.
- **Bounded update impact**: swap and cleanup work should not create large latency spikes.

### Secondary goals as design constraints

- **Multi-model support**: serve multiple model IDs simultaneously (e.g., different agents or tasks).
- **Observability**: metrics + tracing + logs that make swaps debuggable.
- **Extensibility**: plug-in model backends (ONNX Runtime, TensorRT, TorchScript, custom), and custom rollouts (canary/A-B).

## 3) Key concepts and terminology

- **Model artifact**: files needed to run inference (weights, graph, tokenizer, metadata, pre/post steps).
- **Model backend**: runtime that executes the model (CPU/GPU engines, kernels, compilation).
- **Model handle**: an object a request holds while running inference; it “pins” a version.
- **Model version**: immutable snapshot (e.g., `model_id="policy", version="2025-12-24.3"`).
- **Warmup**: run representative inputs to populate caches / compile kernels / stabilize latency.
- **Promotion**: making a version “current” for new requests.
- **Retirement**: old version remains available for in-flight requests until safe to unload.

## 4) The concurrency problem (why this is tricky)

You typically have:

- **Many readers**: inference calls that need the current model fast.
- **Occasional writers**: updates that replace the current model and eventually free old resources.

A naïve approach (take a big mutex around “current model”) can cause:

- **head-of-line blocking** (one slow update stalls all inference)
- tail latency spikes
- deadlocks if callbacks/logging also touch the lock

So most hot-swap designs aim for **lock-free (or near lock-free) reads** and **controlled, asynchronous writes**.

## 5) Common hot-swap architectures (from simplest to strongest)

### A) Global RWLock (simple, but can hurt tail latency)

- Readers take a shared lock during inference.
- Writer takes exclusive lock to swap.

Pros:
- easy to implement
- easy to reason about

Cons:
- writer acquisition may stall readers
- readers still pay lock overhead per request
- risky for strict real-time guarantees

Use when: moderate load, mild latency SLOs, early prototype.

### B) Atomic pointer swap + reference counting (practical & widely used)

Core idea:
- Store the “current model” as an **atomic pointer** to an immutable model object.
- Each request loads the pointer and holds a **reference-counted handle**.
- Swap replaces the atomic pointer; old model is freed only when last handle releases.

This is essentially a friendly version of **RCU-style** reads.

Pros:
- very fast reads (atomic load + refcount)
- writer never blocks readers
- per-request consistency is natural

Cons:
- need careful lifecycle & memory management
- refcount traffic can be noticeable at extreme QPS

Use when: typical real-time inference services and game/sim loops.

### C) RCU / epoch-based reclamation / hazard pointers (high-performance variants)

If refcount overhead is too high, you can use:
- **Epoch-based reclamation**: readers enter an epoch; writer retires old versions and frees after all readers pass the epoch.
- **Hazard pointers**: readers publish what they’re using; writer waits until no hazard points to the old object.

Pros:
- extremely cheap reads (sometimes just a few instructions)

Cons:
- harder to implement correctly
- careful tuning and instrumentation needed

Use when: ultra-high throughput + strict tail latency + strong engineering maturity.

### D) Dual-rail / staged routing (for gradual rollout)

Maintain multiple versions simultaneously and route:
- 100% to old
- canary 1–5% to new
- ramp up gradually

Pros:
- safer deployments
- easy rollback

Cons:
- more resource usage (two models in memory)

Use when: production deployments with reliability requirements.

## 6) The model lifecycle (a robust hot-swap state machine)

A safe update flow usually looks like this:

1. **Fetch**: download or locate artifact (non-blocking to inference).
2. **Verify**: checksum/signature, schema/version compatibility.
3. **Load**: deserialize weights / create backend session/engine.
4. **Initialize**: allocate device buffers, set up pre/post ops.
5. **Warmup**: run representative inputs (avoid “first request is slow”).
6. **Health checks**: correctness checks, latency smoke test, memory sanity.
7. **Promote (atomic swap)**: new version becomes “current” for new requests.
8. **Retire old**: old remains alive while in-flight handles exist.
9. **Reclaim**: free old resources safely after last reader completes.

Important principle: **promotion must be fast** (typically an atomic pointer swap), while everything heavy happens before/after promotion in background threads.

## 7) Consistency semantics you should define up front

Hot-swapping isn’t one behavior — you choose semantics:

- **Per-request consistency (recommended baseline)**:
  - Each inference call uses exactly one model version.
  - New calls after promotion use the new version.
  - In-flight calls continue on the old version.

- **Session/actor stickiness (common in games/sims)**:
  - An “agent” keeps the same model version for N frames or a whole episode.
  - Reduces behavior jitter from version changes mid-episode.

- **Global cutover (rarely safe for strict RT)**:
  - Force all calls to new immediately, potentially cancelling old.
  - Can violate real-time / correctness guarantees.

Write these rules down; they become your API contract.

## 8) Performance pitfalls (what breaks “real-time”)

- **Doing I/O on inference threads**: downloads, disk reads, decompression.
- **Compiling/building engines in the request path**:
  - e.g., TensorRT engine build, kernel compilation, graph optimization.
- **Device memory thrash**: loading a new GPU model can fragment memory and cause stalls.
- **Stop-the-world GC / destructor spikes**:
  - unloading a big model can cause long frees/synchronizations.
- **Lock contention**:
  - even RWLocks can be painful at high concurrency.

Mitigations:
- background loader threads
- warmup before promotion
- bounded work queues, deadlines, and backpressure
- staged cleanup (free in chunks) and asynchronous GPU resource destruction

## 9) Safety and correctness checks (don’t skip these)

### Artifact + config compatibility

Real systems often fail at the boundaries:
- tokenizer version mismatch
- different input tensor shapes
- changed normalization constants
- changed action space / label mapping

Mitigation: treat “model version” as a bundle:
- model engine + pre/post config + metadata schema version

### Atomicity and immutability

Make the promoted model **immutable**:
- no mutable shared buffers
- no “lazy initialization” that writes global state after promotion (unless thread-safe)

### Rollback strategy

If new model is unhealthy:
- keep the old version available
- promote old again (swap back)
- log clearly which version was active for each request

## 10) Observability: what to measure

At minimum:

- **Current model version** per model ID (gauge)
- **Swap events**:
  - swap count
  - swap duration (promotion time should be tiny; load/warmup tracked separately)
- **Inference latency** (histograms): p50/p95/p99/p999 per model version
- **Error rate** per version (load failures, inference failures, validation failures)
- **In-flight handle count** per version (helps detect “can’t reclaim old models”)
- **Resource metrics**:
  - CPU, GPU memory usage per version
  - queue lengths and worker utilization

Also helpful:
- tracing span: `inference(model_id, model_version, backend, device)`
- structured logs for update state transitions

## 11) Minimal architecture sketch (conceptual)

### Components

- **ModelRegistry**: stores current model per model_id (atomic pointer / handle).
- **ModelLoader**: background pipeline (fetch → verify → load → warmup → validate).
- **Router**: chooses which version handles a request (current / canary / sticky).
- **Metrics/Tracing**: instrumentation for all state transitions and inference calls.

### The critical pattern

- Reader (inference):
  - load current model pointer
  - acquire a handle (pins version)
  - run inference
  - release handle

- Writer (update):
  - build new model fully off to the side
  - atomic-swap “current”
  - retire old; free later when safe

This is the heart of “zero downtime” model swapping.

## 12) Practical learning path (what to study to build this well)

- **Concurrency basics**:
  - atomic loads/stores, memory ordering (at least acquire/release)
  - immutability patterns
- **Safe reclamation patterns**:
  - reference counting
  - RCU / epoch-based reclamation
  - hazard pointers
- **Latency engineering**:
  - tail latency causes and measurement
  - avoiding blocking in hot paths
- **GPU/runtime specifics** (if relevant):
  - engine build vs load
  - warmup and stream synchronization
  - memory fragmentation and pooling
- **Operational safety**:
  - canary rollouts and fast rollback
  - metrics-driven promotion criteria

## 13) Suggested references (good starting points)

- **RCU overview**: [Wikipedia: Read-copy-update](https://en.wikipedia.org/wiki/Read-copy-update)
- **Hazard pointers**: [Wikipedia: Hazard pointer](https://en.wikipedia.org/wiki/Hazard_pointer)
- **Epoch-based reclamation**: [Wikipedia: Epoch-based reclamation](https://en.wikipedia.org/wiki/Epoch-based_reclamation)
- **Tail latency**: [“The Tail at Scale” (Jeff Dean & Luiz André Barroso)](https://research.google/pubs/the-tail-at-scale/)

## 14) Where this doc fits in your project

This file is a **learning-oriented spec**: it translates the problem statement into concrete architecture ideas and pitfalls. Once you’re ready, the next step is to turn the lifecycle and semantics above into:

- a small API (load/promote/retire)
- a concurrency-safe “current model” mechanism (atomic swap + safe reclamation)
- metrics + a swap event log


