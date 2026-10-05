"""Capture only the five seeded message-page inputs used by frame_validators."""
import argparse
import json
from pathlib import Path
from differential import Client
from edge_cases import sql


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instances', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--post-capture', type=Path)
    parser.add_argument('--source-capture', type=Path)
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text(encoding='utf-8-sig'))
    if setup['family'] != 'chat':
        raise RuntimeError('Use the disposable chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    room, anchor = setup['labels']['rooms.watercooler'], setup['labels']['messages.busy_060']
    # Undo only messages created by the preceding owned probes so model
    # validators compare identical seeded IDs/versions, not wall-clock writes.
    owned = "SELECT id FROM messages WHERE client_message_id LIKE 'parity-cache-dependencies-target%' OR client_message_id LIKE 'parity-posttext-%'"
    for app, instance in setup['instances'].items():
        client = Client(instance, app, args.output)
        extra = []
        if args.post_capture:
            capture = json.loads(args.post_capture.read_text(encoding='utf-8-sig'))
            extra.extend(int(row['id']) for row in capture['results'][app].values())
        if args.source_capture:
            capture = json.loads((args.source_capture / app / 'responses.json').read_text(encoding='utf-8-sig'))
            extra.append(int(capture['bot-blank-attachment']['headers']['location'].rsplit('/', 1)[1]))
        scoped = owned + (' OR id IN (' + ','.join(str(value) for value in extra) + ')' if extra else '')
        cleanup = f"DELETE FROM boosts WHERE message_id IN ({scoped}); DELETE FROM message_search_index WHERE rowid IN ({scoped}); DELETE FROM action_text_rich_texts WHERE record_type='Message' AND record_id IN ({scoped}); DELETE FROM messages WHERE id IN ({scoped});"
        sql(client, cleanup)
        (client.output / 'fixture-changes.sql').write_text(cleanup + '\n')
        client.login('david', setup['labels'])
        path = f'/rooms/{room}/messages'
        first, _ = client.request('david-message-page', 'GET', path)
        client.request('david-message-page-before', 'GET', path + f'?before={anchor}')
        client.request('david-message-page-after', 'GET', path + f'?after={anchor}')
        client.request('david-page-if-none-match', 'GET', path, headers={'If-None-Match': first['headers']['etag']})
        client.request('david-page-if-modified-since', 'GET', path, headers={'If-Modified-Since': first['headers']['last-modified']})
        (client.output / 'responses.json').write_text(json.dumps(client.records, indent=2) + '\n')


if __name__ == '__main__':
    main()
