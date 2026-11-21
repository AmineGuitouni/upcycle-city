using UnityEngine;
using UpcycleCity.Waste;

namespace UpcycleCity.Ecosystem
{
    /// <summary>
    /// Implemented by every visible, cause-and-effect source of pollution in the city
    /// (citizens, overflowing street bins, factory smokestacks, urban decay lots).
    /// Zero magic spawning: every piece of trash or smog traces back to an IPollutionSource.
    /// </summary>
    public interface IPollutionSource
    {
        string SourceId { get; }
        string DisplayName { get; }
        WasteSourceKind SourceKind { get; }
        Vector3 WorldPosition { get; }
        bool IsActive { get; }
        bool IsMitigated { get; }

        /// <summary>Continuous non-trash pollution load contributed to the district (smokestacks, stench, neglect).</summary>
        float AirPollutionLoad { get; }

        /// <summary>Total physical TrashItems spawned by this source since play started.</summary>
        int TotalTrashEmitted { get; }

        /// <summary>Currently live (uncollected) TrashItems originating from this source.</summary>
        int ActiveTrashCount { get; }

        /// <summary>The district this source belongs to (resolved automatically if unassigned).</summary>
        CityDistrict District { get; }

        void BindDistrict(CityDistrict district);
    }
}
