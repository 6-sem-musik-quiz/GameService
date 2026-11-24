using GameService.Domain.Events;

namespace GameService.Service.Interfaces;

public interface IResultPublisher
{
    Task PublishGameFinishedAsync(GameFinishedEvent gameFinished, CancellationToken cancellationToken = default);
}
