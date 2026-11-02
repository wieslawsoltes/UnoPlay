"""Build the original Midnight skin. Requires Python 3 and Pillow. No source artwork or fonts."""
from io import BytesIO
from pathlib import Path
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED
from PIL import Image, ImageDraw

# 4 by 5 hand-pixelled font. Text.bmp reserves the fifth column for tracking.
alphabet = {
    'A':'699F9','B':'E9E9E','C':'78887','D':'E999E','E':'F8E8F','F':'F8E88',
    'G':'788B7','H':'99F99','I':'E444E','J':'11196','K':'9ACA9','L':'8888F',
    'M':'9FF99','N':'9DB99','O':'69996','P':'E9E88','Q':'699B7','R':'E9EA9',
    'S':'7861E','T':'E4444','U':'99996','V':'99964','W':'999FF','X':'99699',
    'Y':'99644','Z':'F124F','0':'69BD6','1':'4C44E','2':'6912F','3':'E161E',
    '4':'99F11','5':'F8E1E','6':'68E96','7':'F1244','8':'69696','9':'69716',
    '.':'00004',':':'04040','-':'00E00',"'":'44000','!':'44404','_':'0000F',
    '+':'04E40','/':'11248','\\':'88421','[':'C888C',']':'62226','^':'49000',
    '&':'464A5','%':'91249',',':'00048','=':'0F0F0','$':'6CE36','#':'6F6F6',
    '(':'24442',')':'84448','"':'AA000','@':'6BD87','?':'69104','*':'A4E4A',
    '…':'0000B','Å':'496F9','Ö':'A6996','Ä':'A69F9',' ':'00000'
}
body = '#303747'
rim = '#161b27'
bright = '#798497'
rail = '#454f60'
panel = '#161b21'
black = '#0a1012'
green = '#9ded48'
gold = '#ffbd59'
ink = '#182029'
muted = '#6b795d'
silver = '#c4cccf'
files = {}

def sheet(name,w,h,base=body):
    global im,d
    im=Image.new('RGB',(w,h),base); files[name]=im; d=ImageDraw.Draw(im)

def rect(x,y,w,h,c):
    if w>0 and h>0: d.rectangle((x,y,x+w-1,y+h-1),fill=c)

def edge(x,y,w,h,light,shadow):
    d.line((x,y+h-1,x,y,x+w-1,y),fill=light)
    d.line((x+w-1,y,x+w-1,y+h-1,x,y+h-1),fill=shadow)

def grad(x,y,w,h,a,b):
    a=bytes.fromhex(a[1:]); b=bytes.fromhex(b[1:])
    for j in range(h):
        rgb=tuple((p*(h-1-j)+q*j)//max(h-1,1) for p,q in zip(a,b))
        rect(x,y+j,w,1,rgb)

def glyph(x,y,c,col):
    for i,row in enumerate(alphabet.get(c.upper(),alphabet['?'])):
        mask=int(row,16)
        for bit in range(4):
            if mask & (8>>bit): d.point((x+bit,y+i),fill=col)

def text(x,y,label,col=ink,spacing=5):
    for i,c in enumerate(label): glyph(x+i*spacing,y,c,col)

def center(x,y,w,h,label,col=ink):
    text(x+(w-(len(label)*5-1))//2,y+(h-5)//2,label,col)

def frame(x,y,w,h):
    grad(x,y,w,h,'#41495b','#242a38')
    edge(x,y,w,h,bright,'#090c13'); edge(x+1,y+1,w-2,h-2,'#515b6a',rim)
    edge(x+3,y+3,w-6,h-6,'#252a37','#485064')

def well(x,y,w,h):
    rect(x,y,w,h,black); edge(x,y,w,h,rim,bright); edge(x+1,y+1,w-2,h-2,'#101014',rail)

def key(x,y,w,h,down=False):
    grad(x,y,w,h,'#687582' if down else '#d1d7d9','#9099a0' if down else '#737f89')
    edge(x,y,w,h,rim if down else '#e5e8dc',bright if down else '#121925')
    if not down: edge(x+1,y+1,w-2,h-2,'#c1c9cc','#53606f')

def cap(x,y,w,h,label,down=False,lit=False):
    key(x,y,w,h,down)
    if lit: rect(x+3,y+(h-3)//2,2,3,'#5ad615')
    center(x+(2 if lit else 0)+int(down),y+int(down),w,h,label)

def title(x,y,label,active=True):
    frame(x,y,275,14)
    grad(x+4,y+3,267,8,'#484963' if active else '#434958','#272c3b')
    labelwidth=len(label)*5
    for yy in (3,5,7,9):
        d.line((x+17,y+yy,x+131-labelwidth//2,y+yy),fill=('#bc9251' if active else '#6a7380'))
        d.line((x+143+labelwidth//2,y+yy,x+239,y+yy),fill=('#bc9251' if active else '#6a7380'))
    center(x,y,275,14,label,'#fff3c8' if active else '#b8c2c5')
    for px,label in [(5,'+'),(244,'-'),(254,'='),(264,'X')]:
        cap(x+px,y+3,8,8,label)

def thumb(x,y,w,h,pressed):
    key(x,y,w,h,pressed)
    for xx in range(x+w//2-2,x+w//2+3,2):
        d.line((xx,y+3,xx,y+h-4),fill=rail)

def transport(x,y,w,h,kind,pressed=False):
    key(x,y,w,h,pressed)
    cx=x+w//2+int(pressed); cy=y+h//2+int(pressed)
    if kind in ('play','next','back'):
        direction=-1 if kind=='back' else 1
        d.polygon([(cx-3*direction,cy-4),(cx+3*direction,cy),(cx-3*direction,cy+4)],fill=ink)
        if kind!='play': rect(cx+4*direction,cy-4,2,9,ink)
    elif kind=='pause':
        rect(cx-4,cy-4,3,8,ink); rect(cx+1,cy-4,3,8,ink)
    elif kind=='stop': rect(cx-3,cy-3,7,7,ink)
    else:
        d.polygon([(cx-4,cy+1),(cx,cy-3),(cx+4,cy+1)],fill=ink); rect(cx-4,cy+3,9,1,ink)

sheet('main',275,116)
frame(0,0,275,116); title(0,0,'U N O P L A Y')
well(8,19,99,43); well(109,19,157,14)
well(109,38,20,10); well(153,38,15,10)
text(132,41,'KBPS',silver,4); text(169,41,'KHZ',silver,4)
rect(252,93,15,16,rim)
d.polygon([(262,94),(254,102),(260,102),(256,109),(266,99),(260,99)],fill=gold)
edge(13,71,250,12,rim,bright)
for y in (107,109): d.line((15,y,140,y),fill=rail)
for y in (107,109): d.line((150,y,235,y),fill=rail)

sheet('titlebar',344,87)
for y,active in [(0,True),(15,False),(57,True),(72,False)]: title(27,y,'U N O P L A Y',active)
for y,active in [(29,True),(42,False)]:
    title(27,y,'',active); well(104,y+3,90,8); well(251,y+3,19,8)
for x,y,label,down in [(0,0,'+',False),(0,9,'+',True),(9,0,'-',False),(9,9,'-',True),(18,0,'X',False),(18,9,'X',True),(0,18,'=',False),(9,18,'=',True),(0,27,'+',False),(9,27,'+',True)]:
    cap(x,y,9,9,label,down)
well(0,36,17,7)
for x in (17,20,23): key(x,36,3,7)
for base,col in [(304,silver),(312,muted)]:
    rect(base,0,8,43,body)
    for i,l in enumerate('OAIDV'): glyph(base+2,3+8*i,l,col)
for i,(x,y) in enumerate([(304,47),(312,55),(320,62),(328,69),(336,77)]):
    rect(x,y,8,7,rim); glyph(x+2,y+1,'OAIDV'[i],gold)

sheet('cbuttons',136,36)
for j,label in enumerate(['back','play','pause','stop','next']):
    for row in (0,1): transport(23*j,18*row,22 if j==4 else 23,18,label,bool(row))
for row in (0,1): transport(114,16*row,22,16,'eject',bool(row))

sheet('text',155,18,black)
for j,chars in enumerate(['ABCDEFGHIJKLMNOPQRSTUVWXYZ"@','0123456789….:()-\'!_+\\/[]^&%,=$#','ÅÖÄ?*']):
    for i,c in enumerate(chars): glyph(i*5,j*6,c,green)

sheet('nums_ex',108,13,black)
segments=[((2,0),(6,0)),((7,1),(7,5)),((7,7),(7,11)),((2,12),(6,12)),((1,7),(1,11)),((1,1),(1,5)),((2,6),(6,6))]
for i,s in enumerate(['012345','12','01643','01623','5612','05623','056432','012','0123456','015623','','6']):
    for j,((ax,ay),(bx,by)) in enumerate(segments):
        if str(j) in s: d.line((i*9+ax,ay,i*9+bx,by),fill=green)
files['numbers']=im.crop((0,0,99,13))

sheet('monoster',56,24)
for y,col in [(0,green),(12,'#6e7888')]:
    center(0,y,29,12,'STEREO',col); center(29,y,27,12,'MONO',col)
sheet('playpaus',48,9,black)
d.polygon([(1,1),(7,4),(1,7)],fill=green); rect(10,1,2,7,green); rect(14,1,2,7,green); rect(20,2,6,6,green); rect(39,1,2,7,gold)

for name,w,off,track in [('volume',68,0,68),('balance',47,9,38)]:
    sheet(name,w,433)
    for i in range(28):
        yy=i*15; well(off,yy,track,13); rect(off+3,yy+5,track-6,3,panel)
        rect(off+3,yy+5,round((track-6)*i/27),1,'#d3ad5a')
        rect(off+3,yy+6,round((track-6)*i/27),2,'#997442')
    thumb(0,422,14,11,True); thumb(15,422,14,11,False)

sheet('posbar',307,10)
well(0,0,248,10)
for xx in range(4,244,5): rect(xx,4,1,2,'#6f6958')
thumb(248,0,29,10,False); thumb(278,0,29,10,True)

sheet('shufrep',92,85)
for row in range(4):
    cap(0,row*15,28,15,'RPT',bool(row%2),row>=2); cap(28,row*15,47,15,'SHUFFLE',bool(row%2),row>=2)
for row in (0,1):
    for col,name in enumerate(['EQ','PL','EQ','PL']): cap(col*23,61+row*12,23,12,name,col>=2,bool(row))

sheet('eqmain',275,315)
frame(0,0,275,116); title(0,0,'EQUALIZER')
for y,lbl in [(45,'+12'),(70,'+0'),(95,'-12')]: text(39,y,lbl,silver,4)
for x,lbl in zip([13,76,94,112,130,148,166,184,202,220,238],['PRE','60','170','310','600','1K','3K','6K','12K','14K','16K']):
    text(x,106,lbl,silver,4)
for row in (0,1):
    cap(0,116+9*row,9,9,'X',bool(row))
    title(0,134+15*row,'EQUALIZER',not row)
for k in range(4):
    cap(10+k*59,119,26,12,'ON',k>=2,bool(k%2))
    cap(36+k*59,119,32,12,'AUTO',k>=2,bool(k%2))
for i in range(28):
    x=13+(i%14)*15; y=164+(i//14)*65
    well(x,y,14,63); rect(x+5,y+3,4,57,panel)
    for yy in range(y+6,y+58,4): rect(x+5,yy,4,1,('#bc9c5a' if yy>=y+56-i*2 else '#45493d'))
    rect(x+2,y+31,10,1,'#76735c')
for row in (0,1):
    thumb(0,164+12*row,11,11,bool(row)); cap(224,164+12*row,44,12,'PRESETS',bool(row))
well(0,294,113,19)
for x in range(4,111,8): rect(x,296,1,15,'#283325')
for y in range(297,312,4): rect(3,y,107,1,'#283325')
grad(115,294,1,19,'#ffba5a',green); rect(0,314,113,1,'#46583b')

sheet('eq_ex',275,56)
for y,active in [(0,True),(15,False)]:
    title(0,y,'',active); text(15,y+5,'EQUALIZER',silver); well(60,y+3,97,7); well(165,y+3,42,7)
for x in [1,4,7,11,14,17]: key(x,30,3,7)
for row in (0,1):
    cap(1,38+row*9,9,9,'=',bool(row)); cap(11,38+row*9,9,9,'X',bool(row))

sheet('pledit',280,186)
for y,active in [(0,True),(21,False)]:
    for x,w in [(0,25),(26,100),(127,25),(153,25)]:
        grad(x,y,w,20,'#495062','#282e3b')
        for yy in [3,5,7,9]: rect(x,y+yy,w,1,'#ac884e' if active else '#647080')
        rect(x,y+15,w,2,rim); rect(x,y+17,w,1,rail)
    rect(40,y+2,71,11,body); center(26,y,100,14,'PLAYLIST',silver)
    cap(157,y+3,9,9,'='); cap(167,y+3,9,9,'X')
for x,w in [(0,12),(31,20)]:
    rect(x,42,w,29,body); rect(x,42,1,29,bright); rect(x+w-2,42,2,29,rim)
for x,y,w in [(72,42,25),(72,57,25),(99,42,50),(99,57,50)]:
    grad(x,y,w,14,'#4a5263','#282e3b'); edge(x,y,w,14,bright,rim)
    if w==50: cap(x+29,y+3,9,9,'='); cap(x+39,y+3,9,9,'X')
for i in range(2): thumb(52+i*9,53,8,18,bool(i))
for x,label in [(52,'X'),(62,'='),(150,'+')]: cap(x,42,9,9,label,True)
for x,w in [(179,25),(205,75)]:
    rect(x,0,w,38,body); rect(x,36,w,2,rim)
for x,w in [(0,125),(126,150)]:
    grad(x,72,w,38,'#3b4353','#272c39'); rect(x,108,w,2,rim)
for x,l in zip([10,39,68,97],['ADD','REM','SEL','OPT']): cap(x,90,23,13,l)
cap(246,90,25,13,'LIST'); well(170,77,73,14)
for i in range(5): transport(128+10*i,95,10,10,['back','play','pause','stop','next'][i])
for x,labs in [(0,['URL','DIR','FILE']),(54,['ALL','CROP','REM','MISC']),(104,['INV','NONE','ALL']),(154,['SORT','INFO','OPT']),(204,['NEW','SAVE','LOAD'])]:
    for j,label in enumerate(labs):
        for k in (0,1): cap(x+k*23,111+j*19,22,18,label,bool(k))

# The 2.9 video skin uses its own title/frame tiles and five independent pressed sprites.
for kind, caption, marks in [('video','VIDEO',['F','1X','2X','TV','+']),
                             ('mb','BROWSER',['<','>','X','R','M'])]:
    sheet(kind,234,119)
    for y,active in [(0,True),(21,False)]:
        for x,w in [(0,25),(26,100),(127,25),(153,25)]:
            grad(x,y,w,20,'#495062','#282e3b')
            for j in [3,5,7,9]: rect(x,y+j,w,1,'#ac884e' if active else '#647080')
            rect(x,y+15,w,2,rim); rect(x,y+17,w,1,rail)
        rect(50,y+2,52,11,body); center(26,y,100,14,caption,silver)
        cap(167,y+3,9,9,'X')
    for x,w in [(127,11),(139,8)]:
        rect(x,42,w,29,body); rect(x,42,1,29,bright); rect(x+w-2,42,2,29,rim)
    for x,y,w in [(0,42,125),(0,81,125),(127,81,25)]:
        grad(x,y,w,38,'#3b4353','#272c39')
        rect(x,y+36,w,2,rim)
        if w==25 or y==81: rect(x,y+9,w,17,black)
    well(90,51,35,17)
    for k in range(3):
        d.line((117+k*2,114,122,109+k*2),fill=bright)
    cap(148,42,9,9,'X',True)
    for i,mark in enumerate(marks):
        for x,y,down in [(9+i*15,51,False),(158+i*15,42,True)]:
            cap(x,y,15,18,mark if mark not in ['<','>'] else '',down)
            if mark in ['<','>']:
                cx=x+7+int(down); cy=y+9+int(down); step=1 if mark=='>' else -1
                d.polygon([(cx+3*step,cy),(cx-2*step,cy-3),(cx-2*step,cy+3)],fill=ink)

# Generic windows store dynamic title glyphs in separated character cells.
sheet('gen',194,109)
for yy,selected in [(0,True),(21,False)]:
    for xx in [0,26,52,78,104,130]:
        grad(xx,yy,25,20,'#495062','#282e3b')
        if xx not in (52,):
            for j in [3,5,7,9]: rect(xx,yy+j,25,1,'#ac884e' if selected else '#647080')
        rect(xx,yy+15,25,2,rim); rect(xx,yy+17,25,1,rail)
    cap(144,yy+3,9,9,'X')
for xx,yy,ww,hh in [(0,42,125,14),(0,57,125,14),(127,72,25,14)]:
    grad(xx,yy,ww,hh,'#3b4353','#272c39'); rect(xx,yy+12,ww,2,rim)
for xx,ww,hh in [(127,11,29),(139,8,29),(158,11,24),(170,8,24)]:
    rect(xx,42,ww,hh,body);rect(xx,42,1,hh,bright);rect(xx+ww-2,42,2,hh,rim)
cap(148,42,9,9,'X',True)
for yy,letters,color in [(72,'0123456789-.',silver),(80,'0123456789-.',muted),
                         (88,'ABCDEFGHIJKLMNOPQRSTUVWXYZ',silver),(96,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','#9ba6ae')]:
    rect(0,yy,126 if yy<88 else 194,7,'#f000e0')
    for j,letter in enumerate(letters):
        xx=1+6*j;rect(xx,yy,5,7,body);glyph(xx,yy+1,letter,color)

# Use deterministic records so rebuilding the editable artwork is byte-for-byte reproducible.
out=Path(__file__).with_name('Midnight.wsz')
with ZipFile(out,'w') as archive:
    def write(name,data):
        item=ZipInfo(name); item.compress_type=ZIP_DEFLATED; item.external_attr=0o644<<16
        archive.writestr(item,data)
    for name,surface in files.items():
        buf=BytesIO(); surface.save(buf,format='BMP'); write(name+'.bmp',buf.getvalue())
    write('pledit.txt',b'[Text]\nNormal=#a4e663\nCurrent=#ffffff\nNormalBG=#0a1012\nSelectedBG=#394a36\nFont=Arial\n')
