using UnityEngine;

namespace UpcycleCity.Pollution
{
    /// <summary>
    /// Base for components that react to pollution outside the shaders: smoke, sound, lights,
    /// gameplay. Override <see cref="OnPollutionChanged"/>; it receives the level at this object.
    /// </summary>
    public abstract class PollutionResponder : MonoBehaviour
    {
        [Tooltip("Use the pollution at this object's position, zones included, instead of the scene-wide level.")]
        [SerializeField] bool sampleAtPosition = true;

        protected virtual void OnEnable()
        {
            PollutionController.Changed += Respond;
            if (PollutionController.Active != null)
                Respond(PollutionController.Active);
        }

        protected virtual void OnDisable() => PollutionController.Changed -= Respond;

        void Respond(PollutionController controller)
        {
            OnPollutionChanged(sampleAtPosition ? controller.SampleAt(transform.position) : controller.Pollution);
        }

        /// <param name="pollution">0 is clean, 1 is fully polluted.</param>
        protected abstract void OnPollutionChanged(float pollution);
    }
}
