// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Disassemble and print a bounded byte range from a -noanalysis project.
// Usage: -postScript ListingRange.java 0xSTART 0xBYTE_COUNT

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Instruction;

public class ListingRange extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2) {
            println("usage: ListingRange.java 0xSTART 0xBYTE_COUNT");
            return;
        }

        Address start = currentProgram.getAddressFactory().getAddress(args[0]);
        long byteCount = Long.decode(args[1]);
        if (start == null || byteCount <= 0) {
            println("invalid range");
            return;
        }

        Address end = start.add(byteCount - 1);
        disassemble(start);
        for (Instruction instruction = getInstructionAt(start);
             instruction != null && instruction.getAddress().compareTo(end) <= 0;
             instruction = instruction.getNext()) {
            println(instruction.getAddress() + "  " + instruction);
        }
    }
}
