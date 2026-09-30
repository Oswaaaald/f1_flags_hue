"""Events shared by the F1 feed, calibration, effects and local replay."""

from dataclasses import asdict, dataclass, field
import time


@dataclass(frozen=True)
class LiveEvent:
    kind: str
    value: str | int | None = None
    session_key: str | None = None
    session_name: str | None = None
    session_type: str | None = None
    source_utc: str | None = None
    received_at: float = field(default_factory=time.time)
    received_mono: float = field(default_factory=time.monotonic)
    initial: bool = False
    details: dict = field(default_factory=dict)

    def as_dict(self) -> dict:
        return asdict(self)
