using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlDoor.Hikvision
{
    public interface IHikvisionGateway : IDisposable
    {
        /// <summary>
        /// Raised after the SDK alarm callback data has been copied into a managed DTO.
        /// </summary>
        event EventHandler<AlarmEventData> OnAlarmEvent;

        /// <summary>
        /// Logs in to a Hikvision device and returns a managed user id and device metadata.
        /// </summary>
        /// <param name="request">Device address, port and credential information.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Login result containing the SDK user id and device information.</returns>
        Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Logs out an existing SDK user id and releases device-side login resources.
        /// </summary>
        /// <param name="request">Logout request containing the SDK user id.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>A completed task after the logout call has been attempted.</returns>
        Task LogoutAsync(LogoutRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Establishes an alarm channel and optionally binds a managed alarm callback.
        /// </summary>
        /// <param name="request">Alarm setup request containing user id and callback options.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Alarm setup response containing the alarm handle.</returns>
        Task<AlarmSetupResponse> SetAlarmAsync(AlarmSetupRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Closes a previously established alarm channel.
        /// </summary>
        /// <param name="request">Alarm close request containing the alarm handle.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>A completed task after the alarm channel is closed.</returns>
        Task CloseAlarmAsync(AlarmCloseRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries the device-side alarm deployment status for an ACS alarm input.
        /// </summary>
        /// <param name="request">Status request containing user id, channel and alarm input index.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Alarm deployment status read from ACS work status.</returns>
        Task<AlarmDeploymentStatus> GetAlarmDeploymentStatusAsync(AlarmDeploymentStatusRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Adds a person record to the device through SDK remote configuration or ISAPI.
        /// </summary>
        /// <param name="request">Person add request containing user id and person data.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the device accepts the person operation.</returns>
        Task AddPersonAsync(AddPersonRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates or updates a person record through the compatible UserInfo/SetUp provisioning endpoint.
        /// </summary>
        /// <param name="request">Person upsert request containing user id and person data.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the device accepts the upsert operation.</returns>
        Task UpsertPersonAsync(UpsertPersonRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a person record from the device.
        /// </summary>
        /// <param name="request">Person delete request containing user id and employee identity.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the device accepts the delete operation.</returns>
        Task DeletePersonAsync(DeletePersonRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Modifies a person record on the device.
        /// </summary>
        /// <param name="request">Person modify request containing user id and updated person data.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the device accepts the modify operation.</returns>
        Task ModifyPersonAsync(ModifyPersonRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries person records from the device.
        /// </summary>
        /// <param name="request">Person query request containing user id and optional filters.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Structured person query response and raw payload summary.</returns>
        Task<QueryPersonResponse> QueryPersonAsync(QueryPersonRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Uploads one face image for an employee to the device.
        /// </summary>
        /// <param name="request">Face upload request containing user id and face data.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the face upload succeeds.</returns>
        Task UploadFaceAsync(UploadFaceRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes one face image from the device.
        /// </summary>
        /// <param name="request">Face delete request containing user id and employee or face identity.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the face delete succeeds.</returns>
        Task DeleteFaceAsync(DeleteFaceRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries face records from the device.
        /// </summary>
        /// <param name="request">Face query request containing user id and optional filters.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Structured face query response and raw payload summary.</returns>
        Task<QueryFaceResponse> QueryFaceAsync(QueryFaceRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sets access permissions for one or more employees.
        /// </summary>
        /// <param name="request">Permission request containing user id and permission entries.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>A completed task when the permission operation succeeds.</returns>
        Task SetPermissionAsync(SetPermissionRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries access permissions from the device.
        /// </summary>
        /// <param name="request">Permission query request containing user id and optional filters.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Structured permission query response and raw payload summary.</returns>
        Task<QueryPermissionResponse> QueryPermissionAsync(QueryPermissionRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends an access-control gateway command such as open, restore or always-close.
        /// </summary>
        /// <param name="request">Gate control request containing user id, door index and command.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Managed gate-control response.</returns>
        Task<GateControlResponse> ControlGatewayAsync(GateControlRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Captures a generic JPEG picture from the device.
        /// </summary>
        /// <param name="request">Capture request containing user id and channel information.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Captured image bytes and content metadata.</returns>
        Task<CaptureResponse> CapturePictureAsync(CaptureRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Captures a face image and quality metadata from the device.
        /// </summary>
        /// <param name="request">Capture request containing user id and channel information.</param>
        /// <param name="cancellationToken">Cancellation token used before the SDK call is started.</param>
        /// <returns>Captured face image bytes and quality metadata.</returns>
        Task<FaceCaptureResult> CaptureFaceAsync(CaptureRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries historical access-control or alarm event records from the device.
        /// </summary>
        /// <param name="request">Event query request containing user id, time range and optional filters.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Structured event records and raw payload summary.</returns>
        Task<EventQueryResponse> QueryEventRecordAsync(EventQueryRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Sends a generic ISAPI HTTP request through direct HTTP or SDK XML pass-through.
        /// </summary>
        /// <param name="request">ISAPI request including method, path, body and authentication options.</param>
        /// <param name="cancellationToken">Cancellation token controlling timeout and caller cancellation.</param>
        /// <returns>ISAPI response status, headers and body.</returns>
        Task<IsapiResponse> SendIsapiRequestAsync(IsapiRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets structured capability flags for the logged-in device.
        /// </summary>
        /// <param name="request">Capability request containing user id and preferred query path.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Device capabilities used by later lifecycle and synchronization stages.</returns>
        Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(DeviceCapabilitiesRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets basic device metadata for the logged-in device.
        /// </summary>
        /// <param name="request">Device info request containing user id and optional ISAPI path.</param>
        /// <param name="cancellationToken">Cancellation token used before the request is started.</param>
        /// <returns>Basic device model, serial number, version and channel information.</returns>
        Task<DeviceInfo> GetDeviceInfoAsync(DeviceInfoRequest request, CancellationToken cancellationToken = default);

        /// <summary>
        /// Reads the last SDK error code from the current SDK thread context.
        /// </summary>
        /// <returns>The SDK error code returned by NET_DVR_GetLastError.</returns>
        int GetLastErrorCode();

        /// <summary>
        /// Maps an SDK or gateway error code to a human-readable Chinese message.
        /// </summary>
        /// <param name="errorCode">SDK, HTTP or gateway error code.</param>
        /// <returns>Localized error message.</returns>
        string GetErrorMessage(int errorCode);
    }
}
