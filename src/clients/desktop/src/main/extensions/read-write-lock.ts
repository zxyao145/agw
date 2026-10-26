type Waiter = { exclusive: boolean; start(): void };

/** 先进先出的读写锁：读取可以并行，修改独占。A FIFO read/write lock: readers share, writers are exclusive. */
export class ReadWriteLock {
  private readers = 0;
  private writer = false;
  private readonly queue: Waiter[] = [];

  public read<T>(operation: () => Promise<T>): Promise<T> {
    return this.run(false, operation);
  }

  public write<T>(operation: () => Promise<T>): Promise<T> {
    return this.run(true, operation);
  }

  private run<T>(exclusive: boolean, operation: () => Promise<T>): Promise<T> {
    return new Promise<T>((resolve, reject) => {
      this.queue.push({
        exclusive,
        start: () => {
          operation()
            .then(resolve, reject)
            .finally(() => {
              if (exclusive) this.writer = false;
              else this.readers -= 1;
              this.drain();
            });
        },
      });
      this.drain();
    });
  }

  private drain(): void {
    while (this.queue.length > 0 && !this.writer) {
      const next = this.queue[0];
      if (next.exclusive) {
        if (this.readers > 0) return;
        this.queue.shift();
        this.writer = true;
        next.start();
        return;
      }
      this.queue.shift();
      this.readers += 1;
      next.start();
    }
  }
}
