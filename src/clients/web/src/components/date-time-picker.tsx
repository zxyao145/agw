"use client";

import * as React from "react";
import { CalendarIcon } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Calendar } from "@/components/ui/calendar";
import { Input } from "@/components/ui/input";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import {
  formatLocalDateExact,
  formatLocalTimeExact,
  parseApiDateTime,
  replaceLocalDate,
  replaceLocalTime,
} from "@/lib/date-time";

type DateTimePickerProps = {
  id: string;
  value: string;
  onChange: (value: string) => void;
};

function DateTimePicker({ id, value, onChange }: DateTimePickerProps) {
  const [dateOpen, setDateOpen] = React.useState(false);
  const [timeZone, setTimeZone] = React.useState<string>();
  const dateTime = parseApiDateTime(value) ?? new Date();

  React.useEffect(() => {
    setTimeZone(Intl.DateTimeFormat().resolvedOptions().timeZone);
  }, []);

  const handleDateChange = (selectedDate?: Date) => {
    if (!selectedDate) {
      return;
    }

    onChange(replaceLocalDate(dateTime, selectedDate).toISOString());
    setDateOpen(false);
  };

  const handleTimeChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    const nextDateTime = replaceLocalTime(dateTime, event.target.value);
    if (nextDateTime) {
      onChange(nextDateTime.toISOString());
    }
  };

  return (
    <div className="grid grid-cols-[minmax(0,1fr)_8.5rem] gap-2">
      <Popover open={dateOpen} onOpenChange={setDateOpen}>
        <PopoverTrigger asChild>
          <Button
            id={`${id}-date`}
            type="button"
            variant="outline"
            className="justify-start text-left font-normal"
          >
            <CalendarIcon className="text-muted-foreground" />
            {formatLocalDateExact(dateTime)}
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-auto p-0" align="start">
          <Calendar
            mode="single"
            selected={dateTime}
            defaultMonth={dateTime}
            onSelect={handleDateChange}
            timeZone={timeZone}
          />
        </PopoverContent>
      </Popover>

      <Input
        id={`${id}-time`}
        type="time"
        step="1"
        aria-label="Select time"
        value={formatLocalTimeExact(dateTime)}
        onChange={handleTimeChange}
      />
    </div>
  );
}

export { DateTimePicker };
