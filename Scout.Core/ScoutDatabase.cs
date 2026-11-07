using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json.Nodes;
namespace Scout.Core;

// Logical artifact paths preserve imports and old checkpoint references; managed paths
// are database keys, never loose files. Media/model paths are outside these roots.
public sealed class ScoutDatabase {
 readonly string connectionString;
 public string Path {get;}
 public ScoutDatabase(string path) {
  Path=System.IO.Path.GetFullPath(path);Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path,Pooling=false,DefaultTimeout=30}.ToString();
  using var db=Open();using var cmd=db.CreateCommand();
  cmd.CommandText="PRAGMA user_version";if(Convert.ToInt32(cmd.ExecuteScalar())>1)throw new InvalidDataException("This Scout database was created by a newer app version.");
  cmd.CommandText="PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS documents(path TEXT PRIMARY KEY COLLATE NOCASE,kind TEXT NOT NULL,content BLOB NOT NULL); CREATE TABLE IF NOT EXISTS file_results(document TEXT NOT NULL COLLATE NOCASE,ordinal INTEGER NOT NULL,source TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(document,ordinal),FOREIGN KEY(document) REFERENCES documents(path) ON DELETE CASCADE); CREATE INDEX IF NOT EXISTS file_source ON file_results(source); PRAGMA user_version=1;";cmd.ExecuteNonQuery();
 }
 SqliteConnection Open(){var db=new SqliteConnection(connectionString);db.Open();using var c=db.CreateCommand();c.CommandText="PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL;";c.ExecuteNonQuery();return db;}
 static string Key(string path)=>System.IO.Path.GetFullPath(path);
 public bool Exists(string path){using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT 1 FROM documents WHERE path=$p";c.Parameters.AddWithValue("$p",Key(path));return c.ExecuteScalar()!=null;}
 public byte[] Read(string path) {
  using var db=Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="SELECT kind,content FROM documents WHERE path=$p";c.Parameters.AddWithValue("$p",Key(path));
  string kind;byte[] content;using(var r=c.ExecuteReader()){if(!r.Read())throw new FileNotFoundException("Saved Scout data was not found.",path);kind=r.GetString(0);content=(byte[])r[1];}
  if(kind!="report")return content;
  var node=JsonNode.Parse(content)!.AsObject();var rows=new JsonArray();c.CommandText="SELECT payload FROM file_results WHERE document=$p ORDER BY ordinal";
  using(var r=c.ExecuteReader())while(r.Read())rows.Add(JsonNode.Parse(r.GetString(0)));node["files"]=rows;return Encoding.UTF8.GetBytes(node.ToJsonString());
 }
 public void Write(string path,byte[] bytes){using var db=Open();using var tx=db.BeginTransaction();Write(db,tx,path,bytes);tx.Commit();}
 static void Write(SqliteConnection db,SqliteTransaction tx,string path,byte[] bytes) {
  var key=Key(path);string kind=System.IO.Path.GetFileName(path).ToLowerInvariant();JsonArray? files=null;
  if(kind=="report.json") {var report=JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'))!.AsObject();files=report["files"]?.DeepClone().AsArray();if(files!=null){report.Remove("files");bytes=Encoding.UTF8.GetBytes(report.ToJsonString());kind="report";}}
  using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="INSERT INTO documents(path,kind,content) VALUES($p,$k,$b) ON CONFLICT(path) DO UPDATE SET kind=excluded.kind,content=excluded.content";
  c.Parameters.AddWithValue("$p",key);c.Parameters.AddWithValue("$k",kind);c.Parameters.AddWithValue("$b",bytes);c.ExecuteNonQuery();
  c.Parameters.Clear();c.Parameters.AddWithValue("$p",key);c.Parameters.AddWithValue("$n",files?.Count??0);c.CommandText="DELETE FROM file_results WHERE document=$p AND ordinal >= $n";c.ExecuteNonQuery();
  if(files==null)return;
  for(int i=0;i<files.Count;i++){c.Parameters.Clear();c.Parameters.AddWithValue("$p",key);c.Parameters.AddWithValue("$i",i);c.Parameters.AddWithValue("$s",files[i].Text("path"));c.Parameters.AddWithValue("$v",files[i]!.ToJsonString());c.CommandText="INSERT INTO file_results(document,ordinal,source,payload) VALUES($p,$i,$s,$v) ON CONFLICT(document,ordinal) DO UPDATE SET source=excluded.source,payload=excluded.payload WHERE payload<>excluded.payload";c.ExecuteNonQuery();}
 }
 public List<string> List(string root,string filename) {
  using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT path FROM documents";using var r=c.ExecuteReader();var paths=new List<string>();
  while(r.Read()){var p=r.GetString(0);if(PreviewPlanner.Within(p,root)&&System.IO.Path.GetFileName(p).Equals(filename,StringComparison.OrdinalIgnoreCase))paths.Add(p);}return paths;
 }

}
public static class ScoutStorage {
 static readonly object Gate=new();static readonly List<(string Root,ScoutDatabase Db)> Roots=[];
 public static void Register(string root,ScoutDatabase db){root=System.IO.Path.GetFullPath(root);lock(Gate){Roots.RemoveAll(r=>r.Root.Equals(root,StringComparison.OrdinalIgnoreCase));Roots.Add((root,db));}}
 public static void Unregister(string root){lock(Gate)Roots.RemoveAll(r=>r.Root.Equals(System.IO.Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase));}
 public static ScoutDatabase? For(string path){if(string.IsNullOrWhiteSpace(path))return null;lock(Gate)return Roots.OrderByDescending(r=>r.Root.Length).FirstOrDefault(r=>PreviewPlanner.Within(path,r.Root)).Db;}
 public static bool Exists(string path)=>For(path)?.Exists(path)??File.Exists(path);
 public static byte[] ReadBytes(string path)=>For(path)?.Read(path)??File.ReadAllBytes(path);
 public static string ReadText(string path)=>Encoding.UTF8.GetString(ReadBytes(path)).TrimStart('﻿');
 public static void WriteText(string path,string text)=>WriteBytes(path,Encoding.UTF8.GetBytes(text));
 public static void WriteBytes(string path,byte[] bytes){if(For(path) is {} db){db.Write(path,bytes);return;}Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);var tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllBytes(tmp,bytes);File.Move(tmp,path,true);}finally{if(File.Exists(tmp))File.Delete(tmp);}}
 public static void EnsureDirectory(string path){if(For(path)==null)Directory.CreateDirectory(path);}
 public static IEnumerable<string> List(string root,string filename)=>For(root)?.List(root,filename)??(Directory.Exists(root)?Directory.EnumerateFiles(root,filename,SearchOption.AllDirectories):[]);
 public static void Copy(string source,string target)=>WriteBytes(target,ReadBytes(source));
 public static string ExportPreview(string path){if(For(path)==null)return path;var folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Scout-preview",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var html=System.IO.Path.Combine(folder,"preview.html");File.WriteAllBytes(html,ReadBytes(path));var plan=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,"plan.json");if(Exists(plan))File.WriteAllBytes(System.IO.Path.Combine(folder,"plan.json"),ReadBytes(plan));return html;}
}
