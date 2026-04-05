using Moq;
using Nebula.Agent.Data;

namespace Nebula.Agent.Test;

public class CompositePromptRequestRepositoryTest
{
    [Fact]
    public async Task save_async_must_save_request_in_all_stores_when_store_is_registered()
    {
        var request = new PromptRequest
        {
            Id = Guid.NewGuid(),
            Prompt = "hello",
            Classification = "Chat"
        };

        var firstStoreMock = create_store_mock();
        firstStoreMock
            .Setup(store => store.SaveAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        var secondStoreMock = create_store_mock();
        secondStoreMock
            .Setup(store => store.SaveAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(request);

        var repository = new CompositePromptRequestRepository([firstStoreMock.Object, secondStoreMock.Object]);

        var result = await repository.SaveAsync(request);

        Assert.Same(request, result);
        firstStoreMock.Verify(store => store.SaveAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        secondStoreMock.Verify(store => store.SaveAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task update_response_async_must_update_response_in_all_stores_when_store_is_registered()
    {
        var requestId = Guid.NewGuid();
        const string response = "Commands executed";

        var firstStoreMock = create_store_mock();
        firstStoreMock
            .Setup(store => store.UpdateResponseAsync(requestId, response, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptRequest { Id = requestId, Response = response });

        var secondStoreMock = create_store_mock();
        secondStoreMock
            .Setup(store => store.UpdateResponseAsync(requestId, response, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptRequest { Id = requestId, Response = response });

        var repository = new CompositePromptRequestRepository([firstStoreMock.Object, secondStoreMock.Object]);

        var result = await repository.UpdateResponseAsync(requestId, response);

        Assert.NotNull(result);
        Assert.Equal(requestId, result!.Id);
        Assert.Equal(response, result.Response);
        firstStoreMock.Verify(store => store.UpdateResponseAsync(requestId, response, It.IsAny<CancellationToken>()), Times.Once);
        secondStoreMock.Verify(store => store.UpdateResponseAsync(requestId, response, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task get_by_id_async_must_return_first_match_when_request_exists()
    {
        var requestId = Guid.NewGuid();
        var expected = new PromptRequest
        {
            Id = requestId,
            Prompt = "hello",
            Classification = "Chat",
            Response = "hi"
        };

        var firstStoreMock = create_store_mock();
        firstStoreMock
            .Setup(store => store.GetByIdAsync(requestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((PromptRequest?)null);

        var secondStoreMock = create_store_mock();
        secondStoreMock
            .Setup(store => store.GetByIdAsync(requestId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var repository = new CompositePromptRequestRepository([firstStoreMock.Object, secondStoreMock.Object]);

        var result = await repository.GetByIdAsync(requestId);

        Assert.NotNull(result);
        Assert.Equal(expected.Id, result!.Id);
        Assert.Equal(expected.Response, result.Response);
    }

    private static Mock<IPromptRequestStore> create_store_mock() => new();
}
