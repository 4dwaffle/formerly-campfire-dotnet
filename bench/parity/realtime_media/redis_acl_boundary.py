"""Local Redis-only proof of sequential, pipelined and Lua PUBLISH ACL failures."""
import argparse
import json
import pathlib
import socket


class Resp:
    def __init__(self,port):
        self.socket=socket.create_connection(('127.0.0.1',port),timeout=3)
        self.reader=self.socket.makefile('rb')

    def send(self,*commands):
        raw=b''
        for command in commands:
            raw+=b'*'+str(len(command)).encode()+b'\r\n'
            for argument in command:
                value=str(argument).encode()
                raw+=b'$'+str(len(value)).encode()+b'\r\n'+value+b'\r\n'
        self.socket.sendall(raw)

    def reply(self):
        first=self.reader.read(1)
        line=self.reader.readline().rstrip(b'\r\n')
        if first==b'*':return [self.reply() for _ in range(int(line))]
        if first==b'$':
            if int(line)==-1:return None
            result=self.reader.read(int(line));assert self.reader.read(2)==b'\r\n'
            return result.decode()
        if first==b':':return int(line)
        if first in (b'+',b'-'):return {'type':first.decode(),'value':line.decode()}
        raise ValueError((first,line))

    def close(self):
        self.reader.close();self.socket.close()


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port',type=int,default=46379)
    parser.add_argument('--output',type=pathlib.Path,required=True)
    args=parser.parse_args()
    admin=Resp(args.port)
    admin.send(['ACL','SETUSER','boundary','on','>local-test','-@all','+publish','+eval','resetkeys','resetchannels','&first','&third'])
    assert admin.reply()['value']=='OK'
    records=[]
    try:
        for mode in ('sequential','pipeline','lua'):
            listener=Resp(args.port);sender=Resp(args.port)
            try:
                listener.send(['SUBSCRIBE','first','third']);listener.reply();listener.reply()
                sender.send(['AUTH','boundary','local-test']);assert sender.reply()['value']=='OK'
                commands=[['PUBLISH','first','first-'+mode],['PUBLISH','denied','denied-'+mode],['PUBLISH','third','third-'+mode]]
                replies=[]
                if mode=='sequential':
                    for command in commands:
                        sender.send(command);reply=sender.reply();replies.append(reply)
                        if isinstance(reply,dict) and reply['type']=='-':break
                elif mode=='pipeline':
                    sender.send(*commands);replies=[sender.reply() for _ in commands]
                else:
                    sender.send(['EVAL',"local result={};for i=1,#ARGV,2 do result[#result+1]=redis.call('PUBLISH',ARGV[i],ARGV[i+1]);end;return result",'0',*sum(([command[1],command[2]] for command in commands),[])])
                    replies=[sender.reply()]
                # An administrative sentinel gives a deterministic boundary for
                # the complete first/third event sequence without idle sleeps.
                admin.send(['PUBLISH','first','sentinel']);admin.reply()
                packets=[]
                while True:
                    packet=listener.reply()
                    if packet[2]=='sentinel':break
                    packets.append(packet)
                assert any(isinstance(reply,dict) and reply['type']=='-' for reply in replies),replies
                assert [packet[1] for packet in packets]==(['first','third'] if mode=='pipeline' else ['first']),packets
                records.append({'mode':mode,'replies':replies,'delivered':packets,'passed':True})
            finally:listener.close();sender.close()
    finally:
        admin.send(['ACL','DELUSER','boundary']);admin.reply();admin.close()
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps({'redis_major':7,'checks':records},indent=2)+'\n',encoding='utf-8')
    print('PASS ACL boundary: sequential/Lua publish only prefix; pipeline also publishes allowed suffix')


if __name__=='__main__':main()
