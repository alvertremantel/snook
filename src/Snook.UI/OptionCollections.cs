using System.Collections.ObjectModel;

namespace Snook.UI;

public sealed partial class MainWindowViewModel
{
    // Preserve selected item instances when a timer notification refreshes unrelated data.
    private static void ReconcileOptions<T>(ObservableCollection<T> target, IEnumerable<T> source, Func<T, Guid> key)
    {
        var incoming = source.ToArray();
        for (var index = 0; index < incoming.Length; index++)
        {
            var existing = -1;
            for (var candidate = index; candidate < target.Count; candidate++)
                if (key(target[candidate]) == key(incoming[index])) { existing = candidate; break; }
            if (existing < 0) target.Insert(index, incoming[index]);
            else
            {
                if (existing != index) target.Move(existing, index);
                if (!EqualityComparer<T>.Default.Equals(target[index], incoming[index])) target[index] = incoming[index];
            }
        }
        while (target.Count > incoming.Length) target.RemoveAt(target.Count - 1);
    }
}
