// SPDX-License-Identifier: MPL-2.0

namespace NINA.Mcp.Test.Fakes;

/// <summary>A clock that stands still (in UTC); timers and timeouts stay real.</summary>
internal sealed class ToolsFixedTimeProvider : TimeProvider {

    private readonly DateTimeOffset now;

    public ToolsFixedTimeProvider(DateTimeOffset now) {
        this.now = now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override DateTimeOffset GetUtcNow() {
        return now;
    }
}
