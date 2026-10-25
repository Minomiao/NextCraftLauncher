using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace NextCraftLauncher.ViewModels;

/// <summary>支持一次性替换全部内容的集合，避免大量逐条变更通知拖慢界面。</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>批量插入，只发一次变更通知，避免逐条插入拖慢列表。</summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        var added = items as IList<T> ?? items.ToList();
        if (added.Count == 0) return;

        for (var offset = 0; offset < added.Count; offset++)
            Items.Insert(index + offset, added[offset]);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add, (System.Collections.IList)added, index));
    }

    /// <summary>批量移除，只发一次变更通知。</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;

        var removed = new List<T>(count);
        for (var offset = 0; offset < count; offset++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Remove, (System.Collections.IList)removed, index));
    }
}
