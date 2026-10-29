using System.Text.Json;
namespace Scout.Core;

public static class SavedGuide
{
    public static SeriesIdentity Identity(string path,string displayName)
    {
        var data=Data.Read(path);
        Data.Catalog(path);
        var identity=data["series_identity"]?.Deserialize<SeriesIdentity>(Data.Options);
        if(identity==null && Uri.TryCreate(data.Text("source"),UriKind.Absolute,out var source)
            && source.Scheme=="https" && source.Host is "thetvdb.com" or "www.thetvdb.com"
            && data.Int("series_id")>0)
            identity=new("tvdb",data.Int("series_id").ToString(),displayName);
        if(identity==null)throw new InvalidDataException("This guide needs a series_identity with provider, ID and name, or a TVDB source URL and series_id.");
        identity.Validate();
        return identity;
    }

    public static List<Episode> Episodes(string path,SeriesIdentity expected)
    {
        if(Identity(path,expected.Name).Key!=expected.Key)
            throw new InvalidDataException("The saved guide belongs to a different show or provider.");
        return Data.Catalog(path);
    }
}
