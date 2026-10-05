"""Read-only follow-ups for missing-format and conditional GET evidence."""
import argparse
import json
from pathlib import Path
from differential import Client, HERE


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--instances', type=Path, default=HERE / 'runtime/instances.json')
    args = parser.parse_args()
    setup = json.loads(args.instances.read_text())
    if setup.get('family') not in ['chat', 'remediation-chat']:
        raise RuntimeError('Expected isolated chat family')
    args.output.mkdir(parents=True, exist_ok=False)
    labels = setup['labels']
    for app, instance in setup['instances'].items():
        human = Client(instance, app, args.output)
        human.login('david', labels)
        human.request('future-if-modified-since', 'GET', f"/rooms/{labels['rooms.watercooler']}/messages", headers={'If-Modified-Since': 'Mon, 05 Oct 2099 00:00:00 GMT'})
        for subtype in ['opens', 'closeds', 'directs']:
            human.request(subtype + '-index', 'GET', '/rooms/' + subtype)
        human.request('direct-subtype-show', 'GET', f"/rooms/directs/{labels['rooms.david_and_kevin']}")
        bot = Client(instance, app, args.output)
        bot.request('unauthenticated-bot-query-regular-route', 'GET', f"/rooms/{labels['rooms.watercooler']}/messages?bot_key={labels['bot_keys.bender']}")
        human.records.update(bot.records)
        (human.output / 'responses.json').write_text(json.dumps(human.records, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
