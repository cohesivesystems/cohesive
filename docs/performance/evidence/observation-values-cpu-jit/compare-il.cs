using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
static (Dictionary<string,string> Methods,string Version) Read(string path)
{
 using var stream=File.OpenRead(path);using var pe=new PEReader(stream);var md=pe.GetMetadataReader();
 string Name(TypeDefinitionHandle h){var t=md.GetTypeDefinition(h);var parent=t.GetDeclaringType();return parent.IsNil?md.GetString(t.Namespace)+"."+md.GetString(t.Name):Name(parent)+"+"+md.GetString(t.Name);}
 var methods=new Dictionary<string,string>();
 foreach(var handle in md.MethodDefinitions)
 {
  var m=md.GetMethodDefinition(handle);var key=Name(m.GetDeclaringType())+"."+md.GetString(m.Name)+Convert.ToHexString(md.GetBlobBytes(m.Signature));
  var signature=new StringBuilder().Append((int)m.Attributes).Append(':').Append((int)m.ImplAttributes);
  if(m.RelativeVirtualAddress!=0)
  {
   var b=pe.GetMethodBody(m.RelativeVirtualAddress);signature.Append(':').Append(Convert.ToHexString(b.GetILBytes())).Append(':').Append(b.MaxStack).Append(':').Append(b.LocalVariablesInitialized);
   if(!b.LocalSignature.IsNil)signature.Append(':').Append(Convert.ToHexString(md.GetBlobBytes(md.GetStandaloneSignature(b.LocalSignature).Signature)));
   foreach(var e in b.ExceptionRegions)signature.Append(':').Append(e.Kind).Append(',').Append(e.TryOffset).Append(',').Append(e.TryLength).Append(',').Append(e.HandlerOffset).Append(',').Append(e.HandlerLength).Append(',').Append(e.FilterOffset).Append(',').Append(e.CatchType.Kind).Append(',').Append(e.CatchType.IsNil?0:System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(e.CatchType));
  }
  methods.Add(key,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()))));
 }
 string version="";
 foreach(var h in md.GetAssemblyDefinition().GetCustomAttributes())
 {
  var a=md.GetCustomAttribute(h);if(a.Constructor.Kind!=HandleKind.MemberReference)continue;var ctor=md.GetMemberReference((MemberReferenceHandle)a.Constructor);if(ctor.Parent.Kind!=HandleKind.TypeReference)continue;
  if(md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name)!="AssemblyInformationalVersionAttribute")continue;
  var blob=md.GetBlobReader(a.Value);blob.ReadUInt16();version=blob.ReadSerializedString();
 }
 return(methods,version);
}
var a=Read(args[0]);var b=Read(args[1]);var differences=a.Methods.Keys.Union(b.Methods.Keys).Where(k=>a.Methods.GetValueOrDefault(k)!=b.Methods.GetValueOrDefault(k)).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new{baseline=a.Version,restored=b.Version,baseline_methods=a.Methods.Count,restored_methods=b.Methods.Count,differences},new JsonSerializerOptions{WriteIndented=true}));
return differences.Length==0?0:1;
