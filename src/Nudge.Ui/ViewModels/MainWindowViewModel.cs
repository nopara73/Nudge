using System;
using Nudge.Core.Interfaces;
using Nudge.Core.Models;

namespace Nudge.Ui.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel(IScoringService scoringService)
    {
        var sampleShow = new Show
        {
            Id = "ui-sample",
            Name = "Nudge Control Surface",
            Description = "Avalonia front-end prototype for controlling ranking workflows.",
            EstimatedReach = 0.62,
            Episodes =
            [
                new Episode("Athlete performance weekly", "Training and race prep", DateTimeOffset.UtcNow.AddDays(-5)),
                new Episode("Recovery and conditioning", "Systems and routines", DateTimeOffset.UtcNow.AddDays(-18))
            ]
        };

        var score = scoringService.Score(sampleShow, ["athlete", "performance"]);
        ConnectionStatus = $"Core connected (sample score: {score.Score:F2})";
    }

    public string ConnectionStatus { get; }
}
