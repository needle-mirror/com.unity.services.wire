using Unity.Services.Core.Internal;

namespace Unity.Services.Wire.Internal
{
    /// <summary>
    /// Creates <see cref="IWire"/> connections to a chosen address, for the packages that must talk
    /// to a wire endpoint other than the project's cloud environment. The registered
    /// <see cref="IWire"/> component remains the connection to that cloud environment.
    /// </summary>
    public interface IWireFactory : IServiceComponent
    {
        /// <summary>
        /// Opens a new wire connection, independent of the one <see cref="IWire"/> provides.
        /// </summary>
        /// <param name="address">The websocket address to connect to, for example
        /// <c>ws://localhost:1234/v2/ws</c>.</param>
        /// <returns>A <see cref="IWire"/> connected to <paramref name="address"/>.</returns>
        IWire Create(string address);
    }
}
