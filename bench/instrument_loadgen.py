"""Copy the pinned loadgen and expose Cable POST failures, without changing HTTP loads.

Run this script before building bench/Dockerfile.loadgen with the printed directory
as its context. It never builds an image or modifies the reference checkout.
"""

import hashlib
import json
import pathlib
import shutil

ROOT = pathlib.Path(__file__).resolve().parents[1]
SOURCE = ROOT / "reference-rust/bench/loadgen"
DESTINATION = ROOT / "bench/.work/instrumented-loadgen"


def replace_once(source, anchor, replacement, description):
    count = source.count(anchor)
    if count != 1:
        raise RuntimeError(f"{description}: expected one source anchor, found {count}; reference source drifted")
    return source.replace(anchor, replacement, 1)


def instrument(source):
    original = source
    source = replace_once(source, "async fn post_marked(\n", """// Instrumentation counts each attempted request, including failures before HTTP.
#[derive(Default)]
struct PostCounts {
    attempted: AtomicU64,
    connection_errors: AtomicU64,
    transport_errors: AtomicU64,
    statuses: Mutex<HashMap<u16, u64>>,
}

impl PostCounts {
    fn snapshot(&self) -> Value {
        let statuses = self.statuses.lock().unwrap();
        let connection_errors = self.connection_errors.load(Ordering::Relaxed);
        let transport_errors = self.transport_errors.load(Ordering::Relaxed);
        let http_errors: u64 = statuses.iter().filter(|(status, _)| **status >= 400).map(|(_, count)| *count).sum();
        json!({
            "attempted": self.attempted.load(Ordering::Relaxed),
            "connection_errors": connection_errors,
            "transport_errors": transport_errors,
            "http_errors": http_errors,
            "errors": connection_errors + transport_errors + http_errors,
            "statuses": statuses.iter().map(|(status, count)| (status.to_string(), json!(count))).collect::<serde_json::Map<_, _>>(),
        })
    }
}

async fn post_marked(
""", "Cable counter type")
    source = replace_once(source, """    seq: u64,
    delivery: &Delivery,
) -> Option<u64> {
    if sender.is_none() {
        *sender = connect(addr).await.ok();
    }
""", """    seq: u64,
    delivery: &Delivery,
    counts: &PostCounts,
) -> Option<u64> {
    counts.attempted.fetch_add(1, Ordering::Relaxed);
    if sender.is_none() {
        match connect(addr).await {
            Ok(connection) => *sender = Some(connection),
            Err(_) => {
                counts.connection_errors.fetch_add(1, Ordering::Relaxed);
                return None;
            }
        }
    }
""", "Cable connection accounting")
    source = replace_once(source, """        Ok(r) if r.status < 400 => Some(t0.elapsed().as_micros() as u64),
        _ => {
            *sender = None;
            None
        }
""", """        Ok(r) => {
            let elapsed_us = t0.elapsed().as_micros() as u64;
            *counts.statuses.lock().unwrap().entry(r.status).or_default() += 1;
            if r.status < 400 {
                Some(elapsed_us)
            } else {
                *sender = None;
                None
            }
        }
        Err(_) => {
            counts.transport_errors.fetch_add(1, Ordering::Relaxed);
            *sender = None;
            None
        }
""", "Cable status and transport accounting")
    source = replace_once(source, """            let c2 = connected.clone();
            let task = if deflate {
""", """            let c2 = connected.clone();
            let observer_stop = stop.clone();
            let task = if deflate {
""", "Client stop observation")
    source = replace_once(source, """            if let Ok(Err(_)) = task.await {
                failed.fetch_add(1, Ordering::Relaxed);
            }
""", """            match task.await {
                Ok(Ok(())) if observer_stop.load(Ordering::Relaxed) => {}
                _ => { failed.fetch_add(1, Ordering::Relaxed); }
            }
""", "Client failures and premature closure")
    source = replace_once(source, """    let mut poster = None;
    let mut post_h = hist();
""", """    let latency_posts = Arc::new(PostCounts::default());
    let mut poster = None;
    let mut post_h = hist();
""", "Latency phase counters")
    source = replace_once(source,
                          "post_marked(&mut poster, &addr, &room, &cookie, &csrf, seq, &delivery).await",
                          "post_marked(&mut poster, &addr, &room, &cookie, &csrf, seq, &delivery, &latency_posts).await",
                          "Latency request counters")
    source = replace_once(source, """        "messages": latency_msgs,
        "complete": complete,
""", """        "messages": latency_msgs,
        "complete": complete,
        "post_requests": latency_posts.snapshot(),
""", "Latency counter output")
    source = replace_once(source, """    let next = Arc::new(AtomicU64::new(seq + 1));
    phase("saturated");
""", """    let next = Arc::new(AtomicU64::new(seq + 1));
    let throughput_posts = Arc::new(PostCounts::default());
    phase("saturated");
""", "Throughput phase counters")
    source = replace_once(source, """        let (addr, room, cookie, csrf, delivery, next) =
            (addr.clone(), room.clone(), cookie.clone(), csrf.clone(), delivery.clone(), next.clone());
""", """        let (addr, room, cookie, csrf, delivery, next, counts) =
            (addr.clone(), room.clone(), cookie.clone(), csrf.clone(), delivery.clone(), next.clone(), throughput_posts.clone());
""", "Throughput worker counters")
    source = replace_once(source,
                          "post_marked(&mut conn, &addr, &room, &cookie, &csrf, s, &delivery).await",
                          "post_marked(&mut conn, &addr, &room, &cookie, &csrf, s, &delivery, &counts).await",
                          "Throughput request counters")
    source = replace_once(source, """        "posted": tseqs.len(),
        "posts_per_sec": """, """        "posted": tseqs.len(),
        "post_requests": throughput_posts.snapshot(),
        "posts_per_sec": """, "Throughput counter output")
    http_start = "async fn http_load(a: &Args) -> Res<Value> {"
    http_end = "struct Delivery {"
    original_http = original[original.index(http_start):original.index(http_end)]
    patched_http = source[source.index(http_start):source.index(http_end)]
    if original_http != patched_http:
        raise RuntimeError("HTTP load source changed unexpectedly")
    return source


def main():
    source_path = SOURCE / "src/main.rs"
    original = source_path.read_text(encoding="utf-8")
    patched = instrument(original)  # Validate every anchor before creating the copy.
    shutil.copytree(SOURCE, DESTINATION, dirs_exist_ok=True,
                    ignore=shutil.ignore_patterns("target", ".git"))
    (DESTINATION / "src/main.rs").write_text(patched, encoding="utf-8", newline="\n")
    metadata = {
        "source": str(source_path.relative_to(ROOT)),
        "source_sha256": hashlib.sha256(source_path.read_bytes()).hexdigest(),
        "source_normalized_sha256": hashlib.sha256(original.encode()).hexdigest(),
        "patched_sha256": hashlib.sha256(patched.encode()).hexdigest(),
        "http_source_unchanged": True,
        "changes": ["Cable latency and throughput POST counters", "Premature client closure and task failure counting"],
    }
    (DESTINATION / "instrumentation.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    print(DESTINATION)


if __name__ == "__main__":
    main()
