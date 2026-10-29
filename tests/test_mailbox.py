import pytest

from etherlighting_evolved.mailbox import Mailbox


@pytest.mark.anyio
async def test_a_slow_client_gets_every_ack_and_only_the_newest_frame():
    box = Mailbox()
    for i in range(500):
        box.put({"type": "frame", "n": i})
        box.put({"type": "state", "n": i})
        if i < 100:
            box.put({"type": "ack", "id": i})
    assert len(box) == 102

    acks = [(await box.get())["id"] for _ in range(100)]
    assert acks == list(range(100))
    assert await box.get() == {"type": "state", "n": 499}
    assert await box.get() == {"type": "frame", "n": 499}
    assert len(box) == 0


@pytest.mark.anyio
async def test_get_waits_for_the_next_message():
    import asyncio

    box = Mailbox()
    waiting = asyncio.create_task(box.get())
    await asyncio.sleep(0.01)
    assert not waiting.done()
    box.put({"type": "pong"})
    assert await asyncio.wait_for(waiting, 1) == {"type": "pong"}
