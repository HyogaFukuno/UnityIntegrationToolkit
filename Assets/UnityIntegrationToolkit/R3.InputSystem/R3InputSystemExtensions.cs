#if ENABLE_R3_INPUT_SYSTEM_EXTENSIONS

using System.Threading;
using UnityEngine.InputSystem;

namespace R3.InputSystem
{
    public static class R3InputSystemExtensions
    {
        public static Observable<InputAction.CallbackContext> StartedAsObservable(this InputAction inputAction, CancellationToken ct = default)
        {
            return Observable.FromEvent<InputAction.CallbackContext>(
                h => inputAction.started += h,
                h => inputAction.started -= h,
                ct);
        }
        
        public static Observable<InputAction.CallbackContext> PerformedAsObservable(this InputAction inputAction, CancellationToken ct = default)
        {
            return Observable.FromEvent<InputAction.CallbackContext>(
                h => inputAction.performed += h,
                h => inputAction.performed -= h,
                ct);
        }
        
        public static Observable<InputAction.CallbackContext> CanceledAsObservable(this InputAction inputAction, CancellationToken ct = default)
        {
            return Observable.FromEvent<InputAction.CallbackContext>(
                h => inputAction.canceled += h,
                h => inputAction.canceled -= h,
                ct);
        }
        
        public static Observable<InputAction.CallbackContext> CallbacksAsObservable(this InputAction inputAction, CancellationToken ct = default)
        {
            return Observable.FromEvent<InputAction.CallbackContext>(
                h =>
                {
                    inputAction.started += h;
                    inputAction.performed += h;
                    inputAction.canceled += h;
                },
                h =>
                {
                    inputAction.started -= h;
                    inputAction.performed -= h;
                    inputAction.canceled -= h;
                },
                ct);
        }
    }
}

#endif