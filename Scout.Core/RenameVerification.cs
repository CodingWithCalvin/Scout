using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
namespace Scout.Core;

// Same-volume rename changes a directory entry, not the file's contents.
// Keep a read handle that disallows writers for the duration of the operation.
public static class RenameVerification {
 [StructLayout(LayoutKind.Sequential)]
 private struct FileIdInfo {public ulong Volume;public ulong Low;public ulong High;}
 [StructLayout(LayoutKind.Sequential)]
 private struct BasicInfo {
  public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh;
  public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;
 }
 [DllImport("kernel32.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool GetFileInformationByHandleEx(SafeFileHandle file,int informationClass,out FileIdInfo info,uint size);
 [DllImport("kernel32.dll",SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool GetFileInformationByHandle(SafeFileHandle file,out BasicInfo info);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
 [return:MarshalAs(UnmanagedType.Bool)]
 private static extern bool MoveFileExW(string source,string destination,uint flags);
 public static string? Identity(SafeFileHandle file) {
  if(!OperatingSystem.IsWindows())return null;
  if(GetFileInformationByHandleEx(file,18,out var id,24)&&(id.Low!=0||id.High!=0))
   return $"id128:{id.Volume:x16}:{id.High:x16}{id.Low:x16}";
  if(GetFileInformationByHandle(file,out var basic)&&(basic.IndexHigh!=0||basic.IndexLow!=0))
   return $"id64:{basic.Volume:x8}:{basic.IndexHigh:x8}{basic.IndexLow:x8}";
  return null; // Some NAS/SMB servers do not expose a stable file identifier.
 }
 public static FileStream OpenGuard(string path)=>new(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,1,FileOptions.RandomAccess);
 public static JsonObject Capture(string path,SafeFileHandle handle,JsonNode fingerprint)=>new() {
  ["method"]="same-volume-rename-v1",["file_id"]=Identity(handle),
  ["size"]=fingerprint["size"]!.DeepClone(),["mtime_ns"]=fingerprint["mtime_ns"]!.DeepClone(),
  ["sampled_digest"]=fingerprint["sampled_digest"]!.DeepClone()
 };
 public static bool Matches(string path,JsonNode proof,bool requireIdentity) {
  using var guard=OpenGuard(path);
  var savedId=proof.Text("file_id");var currentId=Identity(guard.SafeFileHandle);
  if(savedId.Length>0&&savedId!=currentId||requireIdentity&&savedId.Length==0)return false;
  return guard.Length==proof["size"]!.GetValue<long>()&&PreviewPlanner.MtimeNs(path)==proof["mtime_ns"]!.GetValue<long>()&&PreviewPlanner.SampledDigest(path)==proof.Text("sampled_digest");
 }
 public static void Move(string source,string destination) {
  if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Scout applies media renames on Windows.");
  // No COPY_ALLOWED or REPLACE_EXISTING flags: never silently copy a large file
  // across volumes and never overwrite an existing destination.
  if(!MoveFileExW(LongPath(source),LongPath(destination),0)) {
   int error=Marshal.GetLastWin32Error();
   throw new IOException(error==17?"This move crosses volumes. Scout's fast rename leaves it in place; a copy workflow is required.":new Win32Exception(error).Message);
  }
 }
 private static string LongPath(string path) {
  path=Path.GetFullPath(path);
  if(path.StartsWith(@"\\?\"))return path;
  return path.StartsWith(@"\\")?@"\\?\UNC\"+path[2..]:@"\\?\"+path;
 }
}
