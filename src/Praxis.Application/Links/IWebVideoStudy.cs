namespace Praxis.Application.Links;

public interface IWebVideoStudy
{
    Task<WebVideoSnapshot> StudyAsync(WebVideoSnapshot snapshot, Uri? audio, Uri? video, CancellationToken cancellationToken);
}
