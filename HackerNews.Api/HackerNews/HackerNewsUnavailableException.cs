namespace HackerNews.Api.HackerNews;

public sealed class HackerNewsUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
