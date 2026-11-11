using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sprig.Engine.Definitions;

namespace Sprig.App.ViewModels;

/// <summary>One file waiting for approval, as the review lists it.</summary>
public sealed partial class ReviewFile(PendingTrustItem item) : ObservableObject
{
    public PendingTrustItem Item { get; } = item;

    /// <summary>A repo's sprig.yaml is named by its folder, since every repo's file has the same name.</summary>
    public string Name { get; } = string.Equals(System.IO.Path.GetFileName(item.Path), "sprig.yaml", StringComparison.OrdinalIgnoreCase)
        ? $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path))}/sprig.yaml"
        : System.IO.Path.GetFileName(item.Path);

    public string Path => Item.Path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(Marker))]
    private bool _isApproved;

    [ObservableProperty] private bool _isCurrent;

    public string State => IsApproved ? "approved" : Item.Why;
    public string Marker => IsApproved ? "✓" : "•";
}

/// <summary>
/// Reading and approving definitions one file at a time. Sprig runs each as the user, so approving is a decision about
/// what's on screen: a file that changed after it was checked can't be approved until it's checked again.
/// </summary>
public sealed partial class CodeReviewViewModel(ITrustStore trust) : ViewModelBase
{
    public ObservableCollection<ReviewFile> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApproveLabel), nameof(CanApproveCurrent))]
    private ReviewFile? _current;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _note = "";

    /// <summary>Shown as a dialog, so reading the code gets the whole window.</summary>
    [ObservableProperty] private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApproveCurrent))]
    private bool _isStale;

    /// <summary>Once every file is approved: carry on with whatever was waiting on them.</summary>
    public Func<Task>? AllApproved { get; set; }

    /// <summary>After each approval, so the page can update what it says is left.</summary>
    public event Action? Approved;

    public int Remaining => Files.Count(f => !f.IsApproved);
    public bool HasProblem => Problem.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool CanApproveCurrent => Current is { IsApproved: false } && !IsStale;
    public bool ShowApproveAll => Remaining > 1;
    public string ApproveLabel => Current is { } file ? $"Approve {file.Name}" : "Approve";
    public string ApproveAllLabel => $"Approve all {Remaining}";
    public string Summary => Remaining == 1 ? "1 file to approve" : $"{Remaining} files to approve";
    public string OpenLabel => Remaining == 1 ? "Review 1 file" : $"Review {Remaining} files";

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));
    partial void OnNoteChanged(string value) => OnPropertyChanged(nameof(HasNote));

    public void Load(IEnumerable<PendingTrustItem> items)
    {
        Files.Clear();
        foreach (var item in items) Files.Add(new ReviewFile(item));
        CountsChanged();
        Show(Files.FirstOrDefault());
    }

    [RelayCommand]
    private void Open()
    {
        if (Files.Count == 0) return;
        if (Current is not { IsApproved: false }) Show(Files.FirstOrDefault(f => !f.IsApproved) ?? Files[0]);
        IsOpen = true;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    public void Clear()
    {
        IsOpen = false;
        Files.Clear();
        Current = null;
        Text = "";
        Problem = "";
        IsStale = false;
        CountsChanged();
    }

    [RelayCommand]
    private void Show(ReviewFile? file)
    {
        foreach (var f in Files) f.IsCurrent = f == file;
        Current = file;
        Problem = "";
        IsStale = false;
        Text = "";
        if (file is null) return;
        try
        {
            Text = File.ReadAllText(file.Path);
            if (DefinitionHash.Of(Text) != file.Item.Sha256)
            {
                IsStale = true;
                Problem = "This file changed after sprig checked it. Check again to review the version that's there now.";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            IsStale = true;
            Problem = $"It couldn't be read: {e.Message}";
        }
    }

    [RelayCommand]
    private async Task ApproveCurrent()
    {
        if (Current is not { } file || !CanApproveCurrent) return;
        Approve(file);
        if (Files.FirstOrDefault(f => !f.IsApproved) is { } next) Show(next);
        else await FinishAsync();
    }

    [RelayCommand]
    private async Task ApproveAll()
    {
        foreach (var file in Files.Where(f => !f.IsApproved).ToArray()) Approve(file);
        await FinishAsync();
    }

    void Approve(ReviewFile file)
    {
        trust.Approve(file.Item.TrustPath, file.Item.Sha256);
        file.IsApproved = true;
        CountsChanged();
        OnPropertyChanged(nameof(CanApproveCurrent));
        Approved?.Invoke();
    }

    /// <summary>What carries on may turn up more to approve (an orchestration names its repos only once it has run), so the
    /// dialog stays open for those rather than closing and asking to be opened again.</summary>
    async Task FinishAsync()
    {
        var wasOpen = IsOpen;
        IsOpen = false;
        if (AllApproved is { } next) await next();
        if (wasOpen && Remaining > 0) IsOpen = true;
    }

    void CountsChanged()
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(ShowApproveAll));
        OnPropertyChanged(nameof(ApproveAllLabel));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(OpenLabel));
    }
}
