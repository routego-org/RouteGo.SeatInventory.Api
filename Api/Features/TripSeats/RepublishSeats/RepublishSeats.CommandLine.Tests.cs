namespace Api.Features.TripSeats.RepublishSeats;

/// <summary>How the platform engineer starts the job: the options after the program name.</summary>
public sealed class RepublishSeatsCommandLineTests
{
    [Fact]
    public void Parse_NoRepublishOption_IsNotRequested()
    {
        // Arrange
        string[] args = { "--urls", "http://localhost:5000" };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.False(commandLine.IsRequested);
    }

    [Fact]
    public void Parse_TripsGiven_ReadsEveryTripIdentifier()
    {
        // Arrange
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        string[] args = { "--republish-trips", first + "," + second };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.True(commandLine.IsRequested);
        Assert.NotNull(commandLine.Request);
        Assert.Equal(new[] { first, second }, commandLine.Request.TripIds);
        Assert.False(commandLine.Request.IsAllUpcoming);
    }

    [Fact]
    public void Parse_AllWithCorrelationId_ReadsBoth()
    {
        // Arrange
        string[] args = { "--republish-all", "--correlation-id", "rebuild-42" };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.NotNull(commandLine.Request);
        Assert.True(commandLine.Request.IsAllUpcoming);
        Assert.Equal("rebuild-42", commandLine.Request.CorrelationId);
    }

    [Fact]
    public void Parse_TripsAndAllTogether_IsRejected()
    {
        // Arrange
        string[] args = { "--republish-trips", Guid.NewGuid().ToString(), "--republish-all" };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.True(commandLine.IsRequested);
        Assert.Null(commandLine.Request);
        Assert.NotNull(commandLine.Problem);
    }

    [Fact]
    public void Parse_TripsOptionWithoutIdentifiers_IsRejected()
    {
        // Arrange
        string[] args = { "--republish-trips" };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.Null(commandLine.Request);
        Assert.NotNull(commandLine.Problem);
    }

    [Fact]
    public void Parse_IdentifierThatIsNotATrip_IsRejectedNamingIt()
    {
        // Arrange
        string[] args = { "--republish-trips", "T-101" };

        // Act
        var commandLine = RepublishSeatsCommandLine.Parse(args);

        // Assert
        Assert.Null(commandLine.Request);
        Assert.Contains("T-101", commandLine.Problem);
    }
}
