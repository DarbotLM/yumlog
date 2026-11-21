using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;

if(args.Length<1){Console.Error.WriteLine("usage: MdkgImport <review-folder> [verify]");return;}
// Yumlog CLI location: YUMLOG_CLI_ROOT, else the installed release recorded in %LOCALAPPDATA%\DarbotLabs\Yumlog\current.txt.
var installRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DarbotLabs","Yumlog");
var release=Environment.GetEnvironmentVariable("YUMLOG_CLI_ROOT") is { Length: > 0 } configured ? configured
    : Path.Combine(File.ReadAllText(Path.Combine(installRoot,"current.txt")).Trim(),"Yumlog.Native.Cli");
AssemblyLoadContext.Default.Resolving+=(_,name)=>File.Exists(Path.Combine(release,name.Name+".dll"))?AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(release,name.Name+".dll")):null;
var assembly=AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(release,"Yumlog.Native.Cli.dll"));
var writerType=assembly.GetType("Yumlog.Native.Mdkg+MdkgWriter",true)!;
var jsonOptions=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
var root=args[0];
if(args.Length>1&&args[1]=="verify"){
 var readerType=assembly.GetType("Yumlog.Native.Mdkg+MdkgReader",true)!;
 var verify=assembly.GetType("Yumlog.Native.MdkgIntel",true)!.GetMethod("verifyRunbook")!;
 var results=new List<object>();
 foreach(var file in Directory.GetFiles(root,"*-evidence.mdkg")){
  var id=Path.GetFileNameWithoutExtension(file).Replace("-evidence","");
  var method=readerType.GetMethod("Open")!;var parameters=new object?[method.GetParameters().Length];parameters[0]=file;
  var reader=method.Invoke(null,parameters)!;
  try{
   var book=JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root,id+"-yumlog-runbook.json")),verify.GetParameters()[1].ParameterType,jsonOptions);
   var result=verify.Invoke(null,new[]{reader,book});
   var encoded=JsonSerializer.SerializeToElement(result,result!.GetType(),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
   if(!encoded.GetProperty("verified").GetBoolean())throw new Exception("Native proof verification failed: "+id);
   results.Add(new{film=id,verified=true,totalProofs=encoded.GetProperty("totalProofs").GetInt32(),resolvedProofs=encoded.GetProperty("resolvedProofs").GetInt32(),mismatchedProofs=encoded.GetProperty("mismatchedProofs").GetInt32()});
  }finally{(reader as IDisposable)?.Dispose();}
 }
 File.WriteAllText(Path.Combine(root,"yumlog-native-proof-verification.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
 Console.WriteLine(JsonSerializer.Serialize(results));return;
}
var films=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"yumlog-native-results.json"))).RootElement.GetProperty("films").EnumerateArray().ToArray();
var observations=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"winrt-ocr-results.json"))).RootElement.GetProperty("frames").EnumerateArray().ToArray();
string S(JsonElement value,string name)=>value.GetProperty(name).GetString()??"";
double N(JsonElement value,string name)=>value.GetProperty(name).GetDouble();
long T(double seconds)=>(long)Math.Round(seconds*10_000_000);
long Clock(string value)=>T(TimeSpan.Parse(value.Replace(',','.'),System.Globalization.CultureInfo.InvariantCulture).TotalSeconds);
object Point(double x,double y)=>new{x,y};
object Bounds(double x,double y,double width,double height)=>new{topLeft=Point(x,y),topRight=Point(x+width,y),bottomRight=Point(x+width,y+height),bottomLeft=Point(x,y+height)};
object Attribute(string key,string value)=>new{key,value};
object EmptyDelta()=>new{addNodes=Array.Empty<object>(),updateNodes=Array.Empty<object>(),removeNodeIds=Array.Empty<string>(),addEdges=Array.Empty<object>(),updateEdges=Array.Empty<object>(),removeEdgeIds=Array.Empty<string>()};
void Write(object writer,string method,object payload){var target=writerType.GetMethod(method)!;var native=JsonSerializer.Deserialize(JsonSerializer.Serialize(payload),target.GetParameters()[0].ParameterType,jsonOptions);try{target.Invoke(writer,new[]{native});}catch(TargetInvocationException e){throw e.InnerException??e;}}
foreach(var film in films){
 var id=S(film,"id");var videoSha=S(film,"sha256");
 var samples=observations.Where(frame=>S(frame,"film")==id).OrderBy(frame=>N(frame,"timestampSeconds")).ToArray();
 var byTime=samples.ToDictionary(frame=>T(N(frame,"timestampSeconds")));
 var cues=new List<(long start,long end,string text,int index)>();string captionSha="";
 if(film.TryGetProperty("captions",out var captions)){
  captionSha=S(captions,"sha256");var text=File.ReadAllText(Path.Combine(root,S(captions,"path")));
  foreach(Match match in Regex.Matches(text,@"(?m)(\d{2}:\d{2}:\d{2}[.,]\d{3}) --> (\d{2}:\d{2}:\d{2}[.,]\d{3})[^\r\n]*\r?\n([\s\S]*?)(?=\r?\n\r?\n|\z)"))cues.Add((Clock(match.Groups[1].Value),Clock(match.Groups[2].Value),match.Groups[3].Value.Trim(),cues.Count));
 }
 var times=byTime.Keys.Concat(cues.SelectMany(cue=>new[]{cue.start,cue.end})).Distinct().Order().ToArray();
 var file=Path.Combine(root,id+"-evidence.mdkg");
 var open=writerType.GetMethod("Open")!;var openArgs=new object?[open.GetParameters().Length];openArgs[0]=file;
 var writer=open.Invoke(null,openArgs)!;
 try{
  writerType.GetMethod("AddDefaultLayers")!.Invoke(writer,null);
  foreach(var frame in samples){
   var lines=frame.GetProperty("lines").EnumerateArray().Where(line=>!string.IsNullOrWhiteSpace(S(line,"text"))).Select(line=>new{text=S(line,"text"),words=line.GetProperty("words").EnumerateArray().Where(word=>!string.IsNullOrWhiteSpace(S(word,"text"))).Select(word=>{var b=word.GetProperty("bounds");return new{text=S(word,"text"),confidence=0.0,boundingBox=Bounds(N(b,"x"),N(b,"y"),N(b,"width"),N(b,"height"))};}).ToArray()}).ToArray();
   Write(writer,"WriteOcrText",new{qpc=0L,frameIndex=(int)N(frame,"sourceFrameIndex"),ptsTicks=T(N(frame,"timestampSeconds")),durationTicks=0L,ocr=new{provider="Windows.Media.Ocr/classic-external",isAvailable=frame.GetProperty("isAvailable").GetBoolean(),message="External OS OCR downstream of Yumlog image receipts. Word confidence unavailable; zero is a schema placeholder. Media PTS, not a capture clock; no ASR or reviewer judgment.",text=S(frame,"text"),lines}});
  }
  for(var index=0;index<times.Length;index++){
   var time=times[index];var duration=index+1<times.Length?times[index+1]-time:0;
   var nodes=new List<object>();
   foreach(var cue in cues.Where(cue=>cue.start<=time&&time<cue.end))nodes.Add(new{id=$"{id}:caption:{cue.index}",kind="source.authored-vtt-cue",label=cue.text,confidence=1.0,hasBounds=false,bounds=Bounds(0,0,0,0),firstPtsTicks=cue.start,lastPtsTicks=cue.end,attributes=new[]{Attribute("origin","Existing authored VTT; not ASR, observed OCR or reviewer judgment"),Attribute("vttSha256",captionSha),Attribute("videoSha256",videoSha),Attribute("confidenceMeaning","Exact source-text linkage only; not factual correctness")}});
   if(byTime.TryGetValue(time,out var frame))nodes.Add(new{id=id+":frame:"+(int)N(frame,"sourceFrameIndex"),kind="source.yumlog-image-receipt",label="Sampled rendered frame: "+S(frame,"frame"),confidence=1.0,hasBounds=false,bounds=Bounds(0,0,0,0),firstPtsTicks=time,lastPtsTicks=time,attributes=new[]{Attribute("frameSha256",S(frame,"sha256")),Attribute("videoSha256",videoSha),Attribute("origin","FFmpeg media PTS; native Yumlog hash/dimensions; external classic Windows OCR")}});
   Write(writer,"WriteSemanticGraph",new{qpc=0L,frameIndex=index,ptsTicks=time,durationTicks=duration,graphId="external-video:"+id,isKeyFrame=true,keyFrameNodes=nodes.ToArray(),keyFrameEdges=Array.Empty<object>(),delta=EmptyDelta()});
  }
  writerType.GetMethod("Close")!.Invoke(writer,null);
 }finally{(writer as IDisposable)?.Dispose();}
 Console.WriteLine($"{id}: native MDKG writer imported {samples.Length} OCR frames, {cues.Count} authored caption cues, {times.Length} graph anchors");
}
