// Compile real project sources in memory, with adapters for Unity/Photon services.
// Compare numerical results and event order against a Git revision. No Editor launch.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

string Git(string args) {
    var process = Process.Start(new ProcessStartInfo("git", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
    var result = process.StandardOutput.ReadToEnd(); process.WaitForExit();
    if (process.ExitCode != 0) throw new Exception("git failed: " + args);
    return result;
}
string baseline = Args.Count > 0 ? Args[0] : "8a07abe8c07df7cc06150f1872e741aa4949965d";
// Resolve the revision before using it as part of a git-show argument.
baseline = Git("rev-parse --verify " + baseline).Trim();
string Read(string path, bool original) => original ? Git("show " + baseline + ":" + path) : File.ReadAllText(path);
var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
var stubs = @"
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine {
    public class Object {
        public string name;
        public bool destroyed;
        public static implicit operator bool(Object value) => !ReferenceEquals(value, null) && !value.destroyed;
        public static bool operator ==(Object a, Object b) => (!a && !b) || ReferenceEquals(a,b);
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object other) => ReferenceEquals(this, other);
        public override int GetHashCode() => base.GetHashCode();
        public static void Destroy(Object value) { if (!ReferenceEquals(value, null)) value.destroyed = true; }
    }
    public class GameObject : Object {}
    public class ScriptableObject : Object {}
    public class Coroutine {}
    public class WaitForSeconds { public WaitForSeconds(float seconds) {} }
    public class MonoBehaviour : Object {
        public GameObject gameObject = new GameObject(); public bool isActiveAndEnabled = true;
        protected Coroutine StartCoroutine(IEnumerator routine) => new Coroutine();
        protected void StopCoroutine(Coroutine routine) {}
    }
    public static class Time { public static double timeAsDouble; public static float timeScale = 1f; }
    public static class Debug { public static int exceptions; public static void LogException(Exception e) { exceptions++; } }
    public class SerializeField : Attribute {}
    public class SerializeReference : Attribute {}
    public class SerializeReferenceButton : Attribute {}
    public class MinAttribute : Attribute { public MinAttribute(int n) {} }
    public class RangeAttribute : Attribute { public RangeAttribute(float a,float b) {} }
    public class ColorUsageAttribute : Attribute { public ColorUsageAttribute(bool a,bool b) {} }
    public class CreateAssetMenuAttribute : Attribute { public string fileName, menuName; public int order; }
    public struct Color {
        public float r,g,b,a;
        public static Color white => new Color {r=1,g=1,b=1,a=1};
        public static Color black => new Color {a=1};
        public static Color operator +(Color x,Color y) => new Color {r=x.r+y.r,g=x.g+y.g,b=x.b+y.b,a=x.a+y.a};
        public static Color operator *(Color x,float y) => new Color {r=x.r*y,g=x.g*y,b=x.b*y,a=x.a*y};
    }
    public static class Mathf {
        public static float Max(float a,float b) => a > b ? a : b;
        public static int Max(int a,int b) => a > b ? a : b;
        public static float Min(float a,float b) => a < b ? a : b;
        public static float Abs(float a) => Math.Abs(a);
        public static float Pow(float a,float b) => (float)Math.Pow(a,b);
        public static float Clamp(float x,float lo,float hi) => x < lo ? lo : x > hi ? hi : x;
        public static float Clamp01(float x) => Clamp(x,0,1);
        public static bool Approximately(float a,float b) => Abs(b-a) < Max(0.000001f * Max(Abs(a),Abs(b)), float.Epsilon*8);
        public static float MoveTowards(float a,float b,float amount) => Abs(b-a) <= amount ? b : a + Math.Sign(b-a)*amount;
    }
}
namespace UnityEngine.Events {}
namespace UnityEngine.Localization { public class LocalizedString {} }
namespace Photon.Pun { public class MonoBehaviourPun : MonoBehaviour {} }
namespace Unity.Profiling {
    public struct ProfilerMarker {
        public ProfilerMarker(string name) {} public Scope Auto() => new Scope();
        public struct Scope : IDisposable { public void Dispose() {} }
    }
}
namespace NetStack.Quantization { public static class HalfPrecision { public static ushort Quantize(float f) => 0; public static float Dequantize(ushort n) => 0; } }
namespace NetStack.Serialization {
    public class BitBuffer {
        public void AddUShort(ushort n) {} public void AddByte(byte b) {}
        public ushort ReadUShort() => 0; public byte ReadByte() => 0;
    }
}
public class ModManager {
    public struct ModStub {
        public int loadPriority; public string title;
        public bool GetRepresentedBy(ModStub other) => title == other.title;
    }
}
public class CommandDick { public const short unEquipID = 0; }
public class GenericReagentContainer : GeneHolder {
    public enum InjectType { Inject }
    public ReagentContents mixture = new ReagentContents();
    public int changes;
    public float maxVolume { get => mixture.GetMaxVolume(); set { mixture.SetMaxVolume(value); changes++; } }
    public ReagentContents GetContents() => mixture;
    public float GetVolumeOf(byte id) => mixture.GetVolumeOf(id);
    public float GetVolumeOf(ScriptableReagent asset) => mixture.GetVolumeOf(asset);
    public void OverrideReagent(ScriptableReagent asset,float volume) => mixture.OverrideReagent((byte)ReagentDatabase.GetID(asset),volume);
    public bool AddMix(ReagentContents added,InjectType type) { mixture.AddMix(added,this); changes++; return true; }
    public ReagentContents Metabolize(float f) => mixture.Metabolize(f);
#if OPTIMIZED
    internal void MetabolizeInto(float f,ReagentContents result) => mixture.MetabolizeInto(f,result);
#endif
}
";

// Exercise the actual ProcessReagents body and gene snapshot without adapting the
// entire physics/rendering character. Renderer, Photon and native lifetime tests
// still require Play Mode; the adapters intentionally do not claim to cover them.
string CharacterAdapter(bool original) {
    var genesRoot = CSharpSyntaxTree.ParseText(Read("Assets/KoboldKare/Scripts/KoboldGenes.cs", original), parseOptions).GetRoot();
    var genes = genesRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c=>c.Identifier.Text=="KoboldGenes");
    var members = genes.Members.Where(m => m is FieldDeclarationSyntax || m is StructDeclarationSyntax || m is MethodDeclarationSyntax method && method.Identifier.Text=="With");
    var holder = genesRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().First(c=>c.Identifier.Text=="GeneHolder");
    var kobold = CSharpSyntaxTree.ParseText(Read("Assets/KoboldKare/Scripts/Kobold.cs", original), parseOptions).GetRoot();
    var methodBody = kobold.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m=>m.Identifier.Text=="ProcessReagents");
    var tickBody = kobold.DescendantNodes().OfType<MethodDeclarationSyntax>().First(m=>m.Identifier.Text=="OnMetabolizationEvent");
    return @"using System; using UnityEngine; using Unity.Profiling; using Photon.Pun;
public class KoboldGenes { " + string.Join("\n", members) + " }\n" + holder + @"
public class Kobold : GeneHolder {
    private static readonly ProfilerMarker consumptionMarker = new ProfilerMarker(""consume"");
    private static readonly ProfilerMarker returnMixMarker = new ProfilerMarker(""return"");
    private static readonly ProfilerMarker metabolismMarker = new ProfilerMarker(""tick"");
    private readonly ReagentContents metabolismBuffer = new ReagentContents();
    private bool metabolismInProgress;
    public class View { public bool IsMine = true; }
    public View photonView = new View(); public float stimulation;
    public void Tick(float f) => OnMetabolizationEvent(f);
    public ReagentContents consumedReagents = new ReagentContents(), addbackReagents = new ReagentContents(), metabolizedContents = new ReagentContents();
    public GenericReagentContainer bellyContainer = new GenericReagentContainer();
    public class Ragdoller { public bool ragdolled; }
    public Ragdoller ragdoller = new Ragdoller();
    public float energy; public int visualUpdates, energyEvents;
    public Action<float,float> energyChanged;
    public float GetMaxEnergy() => GetGenes().maxEnergy;
    public override void SetGenes(KoboldGenes genes) {
        visualUpdates++; bellyContainer.maxVolume=genes.bellySize;
        metabolizedContents.SetMaxVolume(genes.metabolizeCapacitySize);
        energyChanged?.Invoke(energy,genes.maxEnergy); base.SetGenes(genes);
    }
" + methodBody + "\n" + tickBody + "\n}";
}

var commonTests = @"
using System;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;
using SimpleJSON;
public static class Checks {
    static List<string> log = new List<string>();
    static ScriptableReagent[] reagents;
    public static void Assert(bool condition,string message) { if (!condition) throw new Exception(message); }
    static void Set(object target,string field,object value) => target.GetType().GetField(field,BindingFlags.Instance|BindingFlags.NonPublic).SetValue(target,value);
    static void Init() {
        new ReagentDatabase().Awake(); new ReactionsDatabase().Awake();
        reagents = new ScriptableReagent[8];
        for(int i=0;i<8;i++) {
            var r = new ScriptableReagent {name=""R""+i};
            Set(r,""metabolizationHalfLife"",i%3==0 ? 0f : (float)(i+1));
            Set(r,""metabolizationFlatRate"",i%2==0 ? 0f : 0.05f);
            Set(r,""calories"",(float)i); Set(r,""consumptionEvent"",new ReagentConsumptionEvent());
            ReagentDatabase.AddAsset(r,null); reagents[i]=r;
        }
    }
    static string State(ReagentContents contents) => contents.Count+"":""+string.Join("","",contents.OrderBy(r=>r.id).Select(r=>r.id+""=""+r.volume.ToString(""R"",CultureInfo.InvariantCulture)));
    sealed class ReactionLog : ReagentReaction {
        public string label; public Action<GenericReagentContainer> action;
        public override void React(GenericReagentContainer c) {log.Add(label); action?.Invoke(c);}
    }
    sealed class Consumption : ReagentConsumptionEvent {
        public int mode;
        public override void OnConsume(Kobold k,ScriptableReagent r,ref float amount,ref ReagentContents memory,ref ReagentContents addBack,ref KoboldGenes genes,ref float energy) {
            base.OnConsume(k,r,ref amount,ref memory,ref addBack,ref genes,ref energy);
            if(mode==0) genes.fatSize+=amount;
            if(mode==1) genes=genes.With(maxEnergy:7,bellySize:100,metabolizeCapacitySize:50);
            amount*=0.25f; addBack.AddMix(7,amount); memory.AddMix(6,amount);
        }
    }
    static ScriptableReagentReaction Recipe(string name,int a,int b,int product,Action<GenericReagentContainer> callback=null) {
        var r=new ScriptableReagentReaction {name=name,
            reactants=new[]{new ScriptableReagentReaction.Reactant {reactant=reagents[a],coefficient=1f},new ScriptableReagentReaction.Reactant {reactant=reagents[b],coefficient=1f}},
            products=new[]{new ScriptableReagentReaction.Reactant {reactant=reagents[product],coefficient=2f}}};
        Set(r,""reactions"",new ReagentReaction[]{new ReactionLog{label=name,action=callback}});
        ReactionsDatabase.AddAsset(r,null); return r;
    }
    public static string Differential() {
        var output=new StringBuilder(); var random=new Random(7182);
        for(int scenario=0;scenario<180;scenario++) {
            Init(); var a=new ReagentContents(); int events=0; a.changed+=_=>events++;
            for(byte id=0;id<8;id++) if(random.Next(3)!=0) a.OverrideReagent(id,(float)random.NextDouble()*50f);
            a.OverrideReagent(200,0.2f); // missing catalog entry, small-volume snap
            for(int tick=0;tick<8;tick++) { var used=a.Metabolize(tick%2==0?2f:0.5f); output.Append(State(a)).Append('|').Append(State(used)).Append(';'); }
            output.Append(events);
            var node=JSONNode.Parse(""{}""); a.Save(node,""mix""); var loaded=new ReagentContents(); loaded.Load(node,""mix""); output.Append(node.ToString()).Append(State(loaded));
        }
        for(int scenario=0;scenario<100;scenario++) {
            Init(); log.Clear();
            // An acyclic product graph keeps recursive reactions finite.
            for(int recipe=0;recipe<12;recipe++) {int p=random.Next(2,8); int a=random.Next(p); int b=random.Next(p); if(a==b) b=(b+1)%p; Recipe(""Recipe""+recipe.ToString(""D2""),a,b,p);}
            var c=new GenericReagentContainer();
            for(byte id=0;id<8;id++) c.mixture.OverrideReagent(id,random.Next(0,6));
            // A zero addition must still trigger ready, unrelated recipes.
            c.mixture.AddMix(7,0,c);
            for(int addition=0;addition<12;addition++) c.mixture.AddMix((byte)random.Next(8),random.Next(0,5),c);
            output.Append(State(c.mixture)).Append(string.Join("","",log)).Append(';');
        }
        Init(); log.Clear();
        Recipe(""A"",0,1,2,c=>{c.OverrideReagent(reagents[3],2);c.OverrideReagent(reagents[4],2);});
        var original=Recipe(""B"",3,4,5); var container=new GenericReagentContainer();
        container.mixture.OverrideReagent(0,1);container.mixture.OverrideReagent(1,1);
        container.mixture.AddMix(7,0,container); output.Append(State(container.mixture)).Append(string.Join("","",log));
        var mod=new ModManager.ModStub {title=""mod"",loadPriority=10};
        var replacement=Recipe(""B"",3,4,6); ReactionsDatabase.RemoveAsset(replacement,null);ReactionsDatabase.AddAsset(replacement,mod);
        container.OverrideReagent(reagents[3],1);container.OverrideReagent(reagents[4],1);container.mixture.AddMix(7,0,container);
        output.Append(State(container.mixture)); ReactionsDatabase.RemoveAsset(replacement,mod);
        container.OverrideReagent(reagents[3],1);container.OverrideReagent(reagents[4],1);container.mixture.AddMix(7,0,container); output.Append(State(container.mixture));
        // Inserting/removing a reagent can shift every catalog ID; cache must refresh.
        var inserted=new ScriptableReagent {name=""A-first""}; ReagentDatabase.AddAsset(inserted,null);
        container.mixture.Clear();container.OverrideReagent(reagents[3],1);container.OverrideReagent(reagents[4],1);container.mixture.AddMix(7,0,container);
        output.Append(State(container.mixture));ReagentDatabase.RemoveAsset(inserted,null);
        for(int unusual=0;unusual<2;unusual++) {
            Init();var recipe=Recipe(""unusual"",0,1,2);var c=new GenericReagentContainer();
            if(unusual==0) {recipe.reactants[1].coefficient=-1;c.mixture.OverrideReagent(1,2);}
            else {c.mixture.OverrideReagent(1,-2);}
            c.mixture.AddMix(7,0,c);output.Append(State(c.mixture));
        }
        Init();
        foreach(bool ragdolled in new[]{false,true}) {
            var k=new Kobold();k.SetGenes(new KoboldGenes());k.ragdoller.ragdolled=ragdolled;
            int genesEvents=0;k.genesChanged+=_=>genesEvents++;k.energyChanged=(a,b)=>k.energyEvents++;
            for(int tick=0;tick<45;tick++) k.ProcessReagents(new ReagentContents());
            output.Append(k.energy.ToString(""R"",CultureInfo.InvariantCulture)).Append('/').Append(genesEvents).Append('/').Append(k.energyEvents).Append('/').Append(k.bellyContainer.changes);
            var food=new ReagentContents();food.AddMix(3,1);k.ProcessReagents(food);
            output.Append(k.energy.ToString(""R"",CultureInfo.InvariantCulture)).Append(State(k.bellyContainer.mixture));
        }
        for(int mode=0;mode<2;mode++) {
            Init();Set(reagents[1],""consumptionEvent"",new Consumption{mode=mode});
            var k=new Kobold();k.SetGenes(new KoboldGenes());k.stimulation=5;
            k.bellyContainer.mixture.OverrideReagent(1,15);
            k.photonView.IsMine=false;k.Tick(2);Assert(k.energy==0&&k.bellyContainer.GetVolumeOf(1)==15,""non-owner metabolized"");
            k.photonView.IsMine=true;
            for(int tick=0;tick<12;tick++) k.Tick(2);
            output.Append(State(k.bellyContainer.mixture)).Append(State(k.metabolizedContents)).Append(State(k.consumedReagents)).Append(k.GetGenes().fatSize.ToString(""R"",CultureInfo.InvariantCulture)).Append(k.GetGenes().maxEnergy).Append(k.energy.ToString(""R"",CultureInfo.InvariantCulture)).Append(k.stimulation);
        }
        Init();var nested=new Kobold();nested.SetGenes(new KoboldGenes());nested.bellyContainer.mixture.OverrideReagent(1,10);
        bool reentered=false;nested.bellyContainer.mixture.changed+=_=>{if(!reentered){reentered=true;nested.Tick(2);}};
        nested.Tick(2);output.Append(State(nested.bellyContainer.mixture)).Append(nested.energy.ToString(""R"",CultureInfo.InvariantCulture));
        return output.ToString();
    }
#if OPTIMIZED
    sealed class Target : UnityEngine.Object { public int calls; public void Tick(float f) {calls++;} }
    public static string Extra() {
        Init();var source=new ReagentContents();var buffer=new ReagentContents();
        source.OverrideReagent(0,40);source.OverrideReagent(1,10);source.MetabolizeInto(2,buffer);
        var first=buffer.First();source.MetabolizeInto(2,buffer);Assert(ReferenceEquals(first,buffer.First()),""entry not reused"");
        source.Clear();source.MetabolizeInto(2,buffer);Assert(buffer.Count==0,""stale empty output"");
        source.OverrideReagent(3,20);source.MetabolizeInto(2,buffer);Assert(buffer.Count==1 && buffer.First().id==3,""stale keys after clear"");
        bool threw=false;try{source.MetabolizeInto(2,source);}catch(ArgumentException){threw=true;}Assert(threw,""source alias accepted"");
        var random=new Random(34);
        for(int scenario=0;scenario<100;scenario++) {
            var a=new ReagentContents();var b=new ReagentContents();
            for(byte id=0;id<8;id++){float v=(float)random.NextDouble()*20;a.OverrideReagent(id,v);b.OverrideReagent(id,v);}
            for(int tick=0;tick<12;tick++){var expected=a.Metabolize(2);b.MetabolizeInto(2,buffer);Assert(State(expected)==State(buffer)&&State(a)==State(b),""buffer differs from allocating API"");}
        }
        var genes=new KoboldGenes();var snapshot=new KoboldGenes.Snapshot(genes);
        Assert(snapshot.Matches(genes.With()),""equal genes rejected"");
        foreach(var field in typeof(KoboldGenes).GetFields(BindingFlags.Instance|BindingFlags.Public)) {
            object old=field.GetValue(genes);object changed=Convert.ChangeType(Convert.ToDouble(old)+1,field.FieldType);
            field.SetValue(genes,changed);Assert(!snapshot.Matches(genes),""snapshot misses ""+field.Name);field.SetValue(genes,old);
        }
        var k=new Kobold();k.SetGenes(genes);k.visualUpdates=0;k.ProcessReagents(new ReagentContents());Assert(k.visualUpdates==0,""unchanged genes reapplied visually"");
        var schedule=new MetabolizationSchedule();var counts=new int[100];int calls=0;
        for(int i=0;i<counts.Length;i++){int id=i;schedule.Add(f=>{Assert(f==2,""wrong step"");counts[id]++;calls++;},0);}
        schedule.Dispatch(1.99,4);Assert(calls==0,""early tick"");
        for(int frame=0;frame<25;frame++){int before=calls;schedule.Dispatch(2+frame*0.01,4);Assert(calls-before==4,""budget/fairness"");}
        Assert(counts.All(n=>n==1),""starvation or duplicate tick"");
        schedule.Dispatch(100,4);int after=calls;schedule.Dispatch(100,4);Assert(calls==after+4,""backlog did not drain"");
        for(int frame=0;frame<25;frame++)schedule.Dispatch(100,4);Assert(counts.All(n=>n==2),""long frame caused catch-up ticks"");
        schedule.Restart(200);schedule.Dispatch(201.9,100);Assert(counts.All(n=>n==2),""restart advanced early"");
        schedule.Dispatch(202,100);Assert(counts.All(n=>n==3),""restart lost listeners"");
        var mutable=new MetabolizationSchedule();int removed=0,added=0;
        DayNightCycle.MetabolizeAction bAction=f=>removed++;DayNightCycle.MetabolizeAction addedAction=f=>added++;
        mutable.Add(f=>{mutable.Remove(bAction);mutable.Add(addedAction,2);},0);mutable.Add(bAction,0);
        mutable.Dispatch(2,10);Assert(removed==0&&added==0,""mutated registration invoked too early"");
        mutable.Dispatch(4,10);Assert(added==1,""added callback lost"");
        var delegates=new MetabolizationSchedule();int aCount=0,bCount=0;
        DayNightCycle.MetabolizeAction aAction=f=>aCount++;bAction=f=>bCount++;
        delegates.Add(aAction+bAction+aAction+bAction,0);delegates.Remove(aAction+bAction);delegates.Dispatch(2,10);Assert(aCount==1&&bCount==1,""multicast subtraction"");
        var target=new Target();delegates=new MetabolizationSchedule();delegates.Add(target.Tick,0);target.destroyed=true;delegates.Dispatch(2,10);Assert(target.calls==0,""destroyed target invoked"");
        delegates.Add(f=>{throw new Exception(""expected"");},2);delegates.Add(aAction,2);delegates.Dispatch(4,10);Assert(aCount==2,""exception stopped siblings"");
        var counterMethod=typeof(GC).GetMethod(""GetAllocatedBytesForCurrentThread"");
        string allocation=""allocation counter unavailable"";
        if(counterMethod!=null) {
            var allocated=(Func<long>)Delegate.CreateDelegate(typeof(Func<long>),counterMethod);
            source.Clear();source.OverrideReagent(1,100);source.MetabolizeInto(2,buffer);
            for(int i=0;i<20;i++){source.OverrideReagent(1,100);source.MetabolizeInto(2,buffer);}
            long start=allocated();for(int i=0;i<1000;i++){source.OverrideReagent(1,100);source.MetabolizeInto(2,buffer);}long reused=allocated()-start;
            start=allocated();for(int i=0;i<1000;i++){source.OverrideReagent(1,100);source.Metabolize(2);}long allocating=allocated()-start;
            Assert(reused<allocating,""no allocation improvement"");allocation=reused+"" vs ""+allocating+"" bytes / 1000 metabolism calls (adapter runtime)"";
        }
        return ""PASS: buffer parity/reuse, gene snapshots, passive energy, scheduling/lifetime/mutations; ""+allocation;
    }
#endif
}
";

Assembly Build(bool original) {
    var files = new[] {"Assets/KoboldKare/Scripts/Database.cs", "Assets/KoboldKare/Scripts/DayNightCycle.cs", "Assets/KoboldKare/Scripts/Reagents/ReagentDatabase.cs", "Assets/KoboldKare/Scripts/Reagents/ReagentContents.cs", "Assets/KoboldKare/Scripts/Reagents/ReactionsDatabase.cs", "Assets/KoboldKare/Scripts/Reagents/ScriptableReagentReaction.cs", "Assets/KoboldKare/Scripts/ReagentReaction.cs", "Assets/KoboldKare/Scripts/Reagents/ScriptableReagent.cs", "Assets/KoboldKare/Scripts/Reagents/ReagentConsumptionEvent.cs", "Assets/ThirdParty/Plugins/SimpleJSON/SimpleJSON.cs"};
    var options = original ? parseOptions : parseOptions.WithPreprocessorSymbols("OPTIMIZED");
    var trees = files.Select(f=>CSharpSyntaxTree.ParseText(Read(f,original),options,path:f)).ToList();
    trees.Add(CSharpSyntaxTree.ParseText(stubs,options,path:"Adapters.cs"));
    trees.Add(CSharpSyntaxTree.ParseText(CharacterAdapter(original),options,path:"CharacterAdapter.cs"));
    trees.Add(CSharpSyntaxTree.ParseText(commonTests,options,path:"Checks.cs"));
    var references = new[]{typeof(object).Assembly.Location,typeof(Enumerable).Assembly.Location,typeof(System.ComponentModel.Component).Assembly.Location}.Distinct().Select(p=>MetadataReference.CreateFromFile(p));
    var compilation = CSharpCompilation.Create("Regression_"+Guid.NewGuid().ToString("N"),trees,references,new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,optimizationLevel:OptimizationLevel.Release));
    using(var stream=new MemoryStream()) {
        var emitted=compilation.Emit(stream);
        if(!emitted.Success) throw new Exception(string.Join("\n",emitted.Diagnostics.Where(d=>d.Severity==DiagnosticSeverity.Error)));
        return Assembly.Load(stream.ToArray());
    }
}

var oldAssembly=Build(true);var newAssembly=Build(false);
object Invoke(Assembly assembly,string method) {
    try { return assembly.GetType("Checks").GetMethod(method).Invoke(null,null); }
    catch(TargetInvocationException e) { throw e.InnerException; }
}
string before=(string)Invoke(oldAssembly,"Differential");string after=(string)Invoke(newAssembly,"Differential");
if(before!=after) {
    int mismatch=Enumerable.Range(0,Math.Min(before.Length,after.Length)).FirstOrDefault(i=>before[i]!=after[i]);
    throw new Exception("Differential mismatch near character "+mismatch+"\nold: "+before.Substring(mismatch,Math.Min(180,before.Length-mismatch))+"\nnew: "+after.Substring(mismatch,Math.Min(180,after.Length-mismatch)));
}
Console.WriteLine("PASS: baseline parity for 180 metabolism/save scenarios, 100 reaction graphs, callbacks, mods, unusual coefficients/volumes, shifted IDs, passive energy, ownership and reentrancy.");
Console.WriteLine(Invoke(newAssembly,"Extra"));

int parsed=0;
foreach(string file in Git("diff --name-only").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).Where(f=>f.EndsWith(".cs"))) {
    foreach(var symbols in new[]{new[]{"UNITY_EDITOR"},new[]{"UNITY_EDITOR","KOBOLDKARE_RUNTIME_PAINTING"}}) {
        var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(file),parseOptions.WithPreprocessorSymbols(symbols),path:file);
        var errors=tree.GetDiagnostics().Where(d=>d.Severity==DiagnosticSeverity.Error).ToArray();
        if(errors.Length>0) throw new Exception(string.Join("\n",errors.Select(d=>d.ToString())));
        if(symbols.Length==1 && tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Any(c=>c.Expression.ToString().Contains("PaintDecal."))) throw new Exception("Painting active by default: "+file);
        parsed++;
    }
}
Console.WriteLine("PASS: "+parsed+" syntax checks, painting off/on; no active PaintDecal calls by default.");
