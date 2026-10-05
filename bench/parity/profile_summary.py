"""Summarize inclusive and exclusive sampled time from an EventPipe Speedscope trace."""
import argparse
import collections
import json
import pathlib

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trace',type=pathlib.Path)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    trace=json.loads(args.trace.read_text())
    names=[frame['name'] for frame in trace['shared']['frames']]
    inclusive=collections.Counter()
    exclusive=collections.Counter()
    total=0
    room=0
    categories=collections.Counter()
    managed_inclusive=collections.Counter()
    managed_exclusive=collections.Counter()
    managed=0
    for profile in trace['profiles']:
        stack=[]
        previous=profile['startValue']
        for event in profile['events']:
            delta=event['at']-previous
            if stack and delta>0:
                total+=delta
                exclusive[names[stack[-1]]]+=delta
                frames={names[index] for index in stack}
                for name in frames:inclusive[name]+=delta
                if names[stack[-1]]=='CPU_TIME':
                    managed+=delta
                    if len(stack)>1:managed_exclusive[names[stack[-2]]]+=delta
                    for name in frames:managed_inclusive[name]+=delta
                if any('ChatRenderer.RoomHtml' in name for name in frames):
                    room+=delta
                    start=next(i for i,index in enumerate(stack) if 'ChatRenderer.RoomHtml' in names[index])
                    render_frames={names[index] for index in stack[start:]}
                    for category,pattern in [('sanitizer','Ganss.Xss'),('HTML parser','AngleSharp'),('SQLite','Sqlite'),('compression','Compression'),('crypto','Cryptography')]:
                        if any(pattern in name for name in render_frames):categories[category]+=delta
            previous=event['at']
            if event['type']=='O':stack.append(event['frame'])
            elif stack and stack[-1]==event['frame']:stack.pop()
            else:raise ValueError('Malformed Speedscope stack')
    result={'trace':str(args.trace),'unit':trace['profiles'][0]['unit'],'total_sampled_time':total,'room_render_sampled_time':room,
            'room_categories':{name:{'inclusive_time':value,'room_percent':value/room*100 if room else 0} for name,value in categories.items()},
            'top_exclusive':[{'frame':name,'time':value,'total_percent':value/total*100} for name,value in exclusive.most_common(40)],
            'top_inclusive':[{'frame':name,'time':value,'total_percent':value/total*100} for name,value in inclusive.most_common(80)]}
    result['managed_sampled_time']=managed
    result['focused_methods']=[{'frame':name,'time':value,'managed_percent':value/managed*100 if managed else 0} for name,value in managed_inclusive.most_common() if any(term in name for term in ('GZip','Deflat','Compression','RailsHttpCompatibility','ChatRenderer.Forms','ChatRenderer.MessageHtml','SqliteDataStore.Open','Utf8','UTF8'))]
    result['top_managed_exclusive']=[{'frame':name,'time':value,'managed_percent':value/managed*100} for name,value in managed_exclusive.most_common(40)]
    result['managed_html_methods']=[{'frame':name,'time':value,'managed_percent':value/managed*100} for name,value in managed_inclusive.most_common() if 'Ganss.Xss' in name or 'AngleSharp' in name][:40]
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({'room_categories':result['room_categories'],'managed_html_methods':result['managed_html_methods'][:12],'top_managed_exclusive':result['top_managed_exclusive'][:8]},indent=2))

if __name__=='__main__':main()
