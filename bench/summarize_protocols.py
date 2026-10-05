"""Validate protocol accounting and summarize both passing and failed runs."""
import argparse
import json
import pathlib
import statistics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory', type=pathlib.Path)
    parser.add_argument('--http-directory', type=pathlib.Path, required=True)
    args = parser.parse_args()
    metadata = json.loads((args.directory / 'metadata.json').read_text())
    http = json.loads((args.http_directory / 'metadata.json').read_text())
    options = metadata['options']
    if metadata['application_source_sha256'] != http['application_source_sha256']:
        raise RuntimeError('HTTP and protocol app sources differ')
    for name in options['apps']:
        if metadata['images'][name]['Id'] != http['images'][name]['Id']:
            raise RuntimeError('HTTP and protocol image IDs differ')
    results = [json.loads((args.directory / f'{name}-{rep}.json').read_text())
               for rep in range(1, options['reps'] + 1) for name in options['apps']]
    stats = {'runs': len(results), 'cable_samples': 0, 'cable_invalid_samples': 0,
             'cable_post_attempts': 0, 'cable_post_errors': 0, 'connection_failures': 0,
             'upload_successes': 0, 'upload_errors': 0, 'database_mismatches': 0,
             'gzip_capture_checks': 0, 'same_source_and_images_as_http': True}
    failed = []
    baseline = next(r for r in results if r['app'] == 'rails')
    for result in results:
        if len(result['cable']) != len(options['cable_clients']):
            raise RuntimeError('Missing Cable sample')
        for route in ('room', 'messages', 'search'):
            if result['captures'][route]['message_ids'] != baseline['captures'][route]['message_ids']:
                raise RuntimeError('Response message IDs differ')
        stats['gzip_capture_checks'] += sum('gzip_response' in c for c in result['captures'].values())
        for sample in result['cable']:
            stats['cable_samples'] += 1
            stats['connection_failures'] += sample['failed']
            if sample.get('validation_error'):
                stats['cable_invalid_samples'] += 1
                failed.append({'app':result['app'], 'repetition':result['repetition'],
                               'clients':sample['clients'], 'reason':sample['validation_error'],
                               'failed_connections':sample['failed'],
                               'complete':sample['throughput']['complete'], 'posted':sample['throughput']['posted']})
            for phase in ('latency','throughput'):
                counts = sample[phase]['post_requests']
                if counts['attempted'] != counts['connection_errors'] + counts['transport_errors'] + sum(counts['statuses'].values()):
                    raise RuntimeError('POST attempt accounting mismatch')
                stats['cable_post_attempts'] += counts['attempted']
                stats['cable_post_errors'] += counts['errors']
        upload = result['upload']
        stats['upload_successes'] += upload['success_count']
        stats['upload_errors'] += upload['error_count']
        db = result['database_validation']
        stats['database_mismatches'] += db['after']['messages'] - db['before']['messages'] != db['expected_writes']
    stats['failed_samples'] = failed
    (args.directory / 'verification.json').write_text(json.dumps(stats,indent=2) + '\n')
    rows = ['# Protocol measurements', '',
            'Same application source and image IDs as the HTTP comparison; three repetitions per build. All connections share one seeded user session.', '',
            '| App | 100 clients: valid runs | 1000 clients: valid runs | 1000-client p99 at 5 messages/s (ms) | Upload + thumbnail median (ms) |',
            '|---|---:|---:|---:|---:|']
    for name in options['apps']:
        runs = [r for r in results if r['app'] == name]
        values = []
        for clients in (100,1000):
            samples = [s for r in runs for s in r['cable'] if s['clients']==clients]
            values.append(f"{sum(not s.get('validation_error') for s in samples)}/{len(samples)}")
        latency = [s['latency']['all_clients']['p99_ms'] for r in runs for s in r['cable'] if s['clients']==1000 and s['latency']['complete']==s['latency']['messages'] and s['latency']['post_requests']['errors']==0]
        latency_cell = f'{statistics.median(latency):.2f}' if latency else 'incomplete'
        upload_times = [s['total_ms'] for r in runs for s in r['upload']['runs'] if s.get('success')]
        rows.append(f"| {name} | {' | '.join(values)} | {latency_cell} | {statistics.median(upload_times):.1f} |")
    rows += ['', 'The latency phase posts 30 messages at 200ms intervals. The overload phase uses four unpaced posting workers for five seconds; a valid run must keep all clients connected and deliver every posted message to every subscriber. Failed overload runs are preserved and are not capacity claims.', '',
             f"{stats['upload_successes']} actual uploads/thumbnails passed, with {stats['upload_errors']} upload errors. The JPEG is resized from 3840x2160 to 1200x675; response MIME, magic, dimensions and hashes are recorded. Encoders and output sizes can differ.", '',
             f"{stats['cable_post_attempts']:,} Cable POST attempts, {stats['cable_post_errors']} POST errors, {stats['connection_failures']:,} premature connection failures, {stats['cable_invalid_samples']} invalid Cable samples. Database message deltas mismatched in {stats['database_mismatches']} runs. All {stats['gzip_capture_checks']} compressed-response checks passed decompression and message-ID validation.", '',
             '[Full fanout rates, losses, response sizes, footprint and raw JSON](report.md). [Aggregate verification and failed samples](verification.json).']
    (args.directory / 'summary.md').write_text('\n'.join(rows) + '\n',encoding='utf-8')
    print(json.dumps(stats))


if __name__ == '__main__':
    main()
