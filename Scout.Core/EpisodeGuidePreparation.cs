namespace Scout.Core;

public static class EpisodeGuidePreparation
{
    public static async Task<List<Episode>> EnsureAsync(string path,SeriesIdentity? identity,IMetadataCatalog? provider,CancellationToken ct) {
        if(File.Exists(path))return identity==null?Data.Catalog(path):SavedGuide.Episodes(path,identity);
        if(identity==null||provider==null)throw new InvalidOperationException("Select a show or import an episode guide before identifying episodes.");
        identity.Validate();
        var episodes=await provider.EpisodesAsync(identity.Id,ct);
        if(episodes.Count==0||episodes.Select(e=>e.Id).Distinct().Count()!=episodes.Count||episodes.Any(e=>string.IsNullOrWhiteSpace(e.Title)||e.Season<0||e.Number<0))
            throw new InvalidDataException("The provider did not return a valid episode guide.");
        Data.Save(path,new{series_identity=identity,episodes});
        return episodes;
    }
}
