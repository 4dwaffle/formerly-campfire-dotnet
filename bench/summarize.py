"""Validate completed raw HTTP runs and render a concise JIT/AOT comparison."""
import argparse
import hashlib
import json
import pathlib
import statistics

from run import ROOT, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=pathlib.Path)
    args = parser.parse_args()
    output = args.directory
    metadata = json.loads((output / 'metadata.json').read_text())
    options = metadata['options']
    results = [json.loads((output / f'{name}-{rep}.json').read_text())
               for rep in range(1, options['reps'] + 1) for name in options['apps']]
    source = hashlib.sha256(b''.join(p.relative_to(ROOT).as_posix().encode() + b'\0' + p.read_bytes()
        for p in sorted((ROOT / 'src/Campfire').rglob('*'))
        if p.is_file() and not {'bin', 'obj'}.intersection(p.parts))).hexdigest()
    if source != metadata['application_source_sha256']:
        raise RuntimeError('Application source changed after benchmark start')
    baseline = next((r for r in results if r['app'] == 'rails'), results[0])
    for result in results:
        if len(result['http']) != 6 * len(options['concurrencies']):
            raise RuntimeError('Incomplete HTTP measurements')
        for route in ('room', 'messages', 'search'):
            if result['captures'][route]['message_ids'] != baseline['captures'][route]['message_ids']:
                raise RuntimeError('Captured message ID mismatch')
        for sample in result['http'] + result['warmups']:
            allowed = {'200', '201', '204'} if sample['route'] == 'post_message' else {'200'}
            if sample['errors'] or not sample['statuses'] or not set(sample['statuses']).issubset(allowed):
                raise RuntimeError('HTTP/transport error in measured or warmup sample')
        db = result['database_validation']
        for count in ('messages', 'bench_fts_rows'):
            if db['after'][count] - db['before'][count] != db['expected_writes']:
                raise RuntimeError('Persisted message/FTS count mismatch')
    report(output, results)
    verification = {'runs': len(results), 'measured_samples': sum(len(r['http']) for r in results),
        'warmup_samples': sum(len(r['warmups']) for r in results),
        'measured_responses': sum(sum(s['statuses'].values()) for r in results for s in r['http']),
        'warmup_responses': sum(sum(s['statuses'].values()) for r in results for s in r['warmups']),
        'persisted_writes': sum(r['database_validation']['expected_writes'] for r in results),
        'transport_errors': 0, 'unexpected_statuses': 0, 'all_message_ids_matched': True,
        'all_message_and_fts_deltas_matched': True, 'source_hash_matched': True}
    (output / 'verification.json').write_text(json.dumps(verification, indent=2) + '\n')
    rows = ['# JIT and Native AOT comparison', '',
        'Three repetitions; medians below. HTTP workloads use 16 concurrent clients. This is the same current application source in both .NET images.', '',
        '| Metric | JIT | Native AOT | AOT change |', '|---|---:|---:|---:|']
    def metric(name, route):
        return statistics.median(s['rps'] for r in results if r['app'] == name for s in r['http'] if s['route'] == route and s['conc'] == 16)
    for label, route in [('Room requests/s', 'room'), ('Pagination requests/s', 'messages'), ('Search requests/s', 'search'), ('Message writes/s', 'post_message')]:
        jit, aot = metric('dotnet', route), metric('aot', route)
        rows.append(f'| {label} | {jit:,.1f} | {aot:,.1f} | {(aot/jit-1)*100:+.1f}% |')
    def memory_mib(value):
        amount, unit = value.split('/')[0].strip(), None
        for suffix, multiplier in [('GiB',1024),('MiB',1),('KiB',1/1024),('B',1/1048576)]:
            if amount.endswith(suffix):
                return float(amount[:-len(suffix)]) * multiplier
        raise RuntimeError('Unknown memory unit')
    for label, func in [
        ('Container start to healthy (ms)', lambda name: statistics.median(r['cold_start_ms'] for r in results if r['app']==name)),
        ('Peak sampled memory (MiB)', lambda name: statistics.median(max(memory_mib(s['MemUsage']) for s in r['resource_samples'] if s['Name'].endswith('-app')) for r in results if r['app']==name))]:
        jit, aot = func('dotnet'), func('aot')
        rows.append(f'| {label} | {jit:,.1f} | {aot:,.1f} | {(aot/jit-1)*100:+.1f}% |')
    rows += ['', f"All {verification['measured_samples']} measured samples and {verification['warmup_samples']} warmups passed: {verification['measured_responses']:,} measured responses, {verification['persisted_writes']:,} persisted writes, zero transport errors or unexpected statuses. Message ID sets and message/search-index deltas matched.", '',
        'Native AOT reduces the measured memory footprint and readiness time, while page rendering is slower in this workload. Writes are variable; inspect the ranges in the full report.', '',
        'Startup includes Docker launch/readiness polling. Memory is the median of per-run peak samples under different achieved throughput, not a matched-arrival-rate footprint comparison.', '',
        'The [full report](report.md) includes Rails, Rust, the preserved original .NET snapshot, latency, response sizes, errors and image sizes. JSON files contain the raw measurements; [verification](verification.json) records the aggregate checks.', '',
        'Cross-implementation HTML and architecture differ: Rust caches fragments and compressed pieces, .NET renders/compresses per request, sidebar responses differ significantly, and equal SQLite durability settings have not been established. This does not certify full Rails parity or production capacity. See [methodology](../../README.md).']
    (output / 'summary.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')
    print(json.dumps(verification))


if __name__ == '__main__':
    main()
