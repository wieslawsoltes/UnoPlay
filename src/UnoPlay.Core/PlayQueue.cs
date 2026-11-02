using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace UnoPlay.Core;

public sealed record Track(string Location, string Title, TimeSpan? Duration = null, object? Source = null);

/// <summary>A queue with one shuffle draw per entry per cycle and reversible navigation.</summary>
public sealed class PlayQueue
{
    // Slots distinguish two occurrences of the same track, including identical object references.
    private sealed class Slot(Track track) { public Track Track { get; } = track; }
    private readonly List<Slot> _slots = [];
    private readonly List<Slot> _remaining = [];
    private readonly List<Slot> _history = [];
    private readonly Random _random;
    private int _cursor = -1;
    private Slot? _current;
    private bool _shuffle;

    public PlayQueue(Random? random = null)
    {
        _random = random ?? Random.Shared;
        Tracks.CollectionChanged += OnCollectionChanged;
    }

    public ObservableCollection<Track> Tracks { get; } = [];
    public Track? Current => _current?.Track;
    public int CurrentIndex => _current is null ? -1 : _slots.FindIndex(s => ReferenceEquals(s, _current));
    public bool Repeat { get; set; }
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            ResetNavigation();
        }
    }

    public Track? Select(int index)
    {
        if ((uint)index >= (uint)_slots.Count) return null;
        Visit(_slots[index]);
        return Current;
    }

    public Track? Next(bool manual = true)
    {
        if (_slots.Count == 0) return null;
        if (!_shuffle)
        {
            int index = CurrentIndex + 1;
            return index < _slots.Count ? Select(index) : Repeat || manual ? Select(0) : null;
        }

        if (_cursor + 1 < _history.Count)
            return (_current = _history[++_cursor]).Track;

        if (_remaining.Count == 0)
        {
            if (!Repeat && !manual) return null;
            _remaining.AddRange(_slots);
            if (_slots.Count > 1) _remaining.RemoveAll(s => ReferenceEquals(s, _current));
        }
        Visit(_remaining[_random.Next(_remaining.Count)]);
        return Current;
    }

    public Track? Previous()
    {
        if (_slots.Count == 0) return null;
        if (!_shuffle)
            return Select(CurrentIndex > 0 ? CurrentIndex - 1 : _slots.Count - 1);
        if (_cursor > 0) _current = _history[--_cursor];
        return Current ?? Next();
    }

    public void Move(int from, int to) => Tracks.Move(from, to);
    public void Clear() => Tracks.Clear();
    public void RemoveAt(int index) => Tracks.RemoveAt(index);

    private void Visit(Slot slot)
    {
        _current = slot;
        if (!_shuffle) return;
        _remaining.RemoveAll(s => ReferenceEquals(s, slot));
        if (_cursor + 1 < _history.Count) _history.RemoveRange(_cursor + 1, _history.Count - _cursor - 1);
        if (_cursor >= 0 && ReferenceEquals(_history[_cursor], slot)) return;
        _history.Add(slot);
        _cursor = _history.Count - 1;
        // Retain a full cycle of back navigation without growing forever on repeat.
        if (_history.Count > Math.Max(256, _slots.Count * 2))
        {
            _history.RemoveRange(0, _slots.Count);
            _cursor -= _slots.Count;
        }
    }

    private void ResetNavigation()
    {
        _history.Clear();
        _remaining.Clear();
        _cursor = -1;
        if (!_shuffle) return;
        _remaining.AddRange(_slots);
        if (_current is not null) Visit(_current);
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _slots.Clear();
            _current = null;
            ResetNavigation();
            return;
        }
        if (e.Action == NotifyCollectionChangedAction.Move)
        {
            var slot = _slots[e.OldStartingIndex];
            _slots.RemoveAt(e.OldStartingIndex);
            _slots.Insert(e.NewStartingIndex, slot);
            return;
        }
        if (e.OldItems is { } oldItems)
        {
            foreach (var _ in oldItems)
            {
                var slot = _slots[e.OldStartingIndex];
                _slots.RemoveAt(e.OldStartingIndex);
                _remaining.RemoveAll(s => ReferenceEquals(s, slot));
                for (int i = _history.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(_history[i], slot)) continue;
                    _history.RemoveAt(i);
                    if (i <= _cursor) _cursor--;
                }
                if (ReferenceEquals(_current, slot)) _current = null;
            }
        }
        if (e.NewItems is { } added)
        {
            int index = e.NewStartingIndex;
            foreach (Track track in added)
            {
                var slot = new Slot(track);
                _slots.Insert(index++, slot);
                if (_shuffle) _remaining.Add(slot);
            }
        }
    }
}
