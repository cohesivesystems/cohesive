------------------------ MODULE ReservationProtocol ------------------------
EXTENDS AssuranceClaim
CONSTANTS ClientCount, OperationCount, MaxAttempts, InitialStock, Quantity, Mode
ASSUME /\ ClientCount \in Nat \ {0} /\ OperationCount \in Nat \ {0}
       /\ MaxAttempts \in Nat /\ InitialStock \in Nat /\ Quantity \in Nat \ {0}
       /\ Mode \in {"AtomicReceipt", "UnprotectedRetry", "SplitReceipt"}
Clients == 1..ClientCount
Operations == 1..OperationCount
VARIABLES stock, receipts, effects, phase, attempts, operation, lost, replayed
vars == <<stock, receipts, effects, phase, attempts, operation, lost, replayed>>

Init == /\ stock = InitialStock
        /\ receipts = {}
        /\ effects = <<>>
        /\ phase = [c \in Clients |-> "ready"]
        /\ attempts = [c \in Clients |-> 0]
        /\ operation \in [Clients -> Operations]
        /\ lost = {}
        /\ replayed = {}

Start(c) == /\ phase[c] = "ready" /\ attempts[c] < MaxAttempts
            /\ phase' = [phase EXCEPT ![c] = "pending"]
            /\ attempts' = [attempts EXCEPT ![c] = @ + 1]
            /\ UNCHANGED <<stock, receipts, effects, operation, lost, replayed>>

Rollback(c) == /\ phase[c] = "pending"
               /\ phase' = [phase EXCEPT ![c] = "ready"]
               /\ UNCHANGED <<stock, receipts, effects, attempts, operation, lost, replayed>>

Replay(c) == /\ phase[c] = "pending" /\ Mode # "UnprotectedRetry"
             /\ operation[c] \in receipts
             /\ phase' = [phase EXCEPT ![c] = "response"]
             /\ replayed' = IF operation[c] \in lost THEN replayed \cup {operation[c]} ELSE replayed
             /\ UNCHANGED <<stock, receipts, effects, attempts, operation, lost>>

Commit(c) == /\ phase[c] = "pending" /\ stock >= Quantity
             /\ (Mode = "UnprotectedRetry" \/ operation[c] \notin receipts)
             /\ stock' = stock - Quantity
             /\ effects' = Append(effects, operation[c])
             /\ receipts' = IF Mode = "AtomicReceipt"
                             THEN receipts \cup {operation[c]} ELSE receipts
             /\ phase' = [phase EXCEPT ![c] = IF Mode = "SplitReceipt"
                                              THEN "unreceipted" ELSE "response"]
             /\ UNCHANGED <<attempts, operation, lost, replayed>>

WriteReceipt(c) == /\ phase[c] = "unreceipted"
                   /\ receipts' = receipts \cup {operation[c]}
                   /\ phase' = [phase EXCEPT ![c] = "response"]
                   /\ UNCHANGED <<stock, effects, attempts, operation, lost, replayed>>

CrashBeforeReceipt(c) == /\ phase[c] = "unreceipted"
                         /\ phase' = [phase EXCEPT ![c] = "ready"]
                         /\ lost' = lost \cup {operation[c]}
                         /\ UNCHANGED <<stock, receipts, effects, attempts, operation, replayed>>

LoseAcknowledgement(c) == /\ phase[c] = "response"
                         /\ phase' = [phase EXCEPT ![c] = "ready"]
                         /\ lost' = lost \cup {operation[c]}
                         /\ UNCHANGED <<stock, receipts, effects, attempts, operation, replayed>>

Acknowledge(c) == /\ phase[c] = "response"
                 /\ phase' = [phase EXCEPT ![c] = "finished"]
                 /\ UNCHANGED <<stock, receipts, effects, attempts, operation, lost, replayed>>

Next == \E c \in Clients : Start(c) \/ Rollback(c) \/ Replay(c) \/ Commit(c)
         \/ WriteReceipt(c) \/ CrashBeforeReceipt(c) \/ LoseAcknowledgement(c) \/ Acknowledge(c)
Spec == Init /\ [][Next]_vars

TypeOK == /\ stock \in 0..InitialStock /\ receipts \subseteq Operations
          /\ effects \in Seq(Operations) /\ Len(effects) <= ClientCount * MaxAttempts
          /\ phase \in [Clients -> {"ready", "pending", "response", "unreceipted", "finished"}]
          /\ attempts \in [Clients -> 0..MaxAttempts]
          /\ operation \in [Clients -> Operations]
          /\ lost \subseteq Operations /\ replayed \subseteq Operations
StockAccounting == stock = InitialStock - Quantity * Len(effects)
Claim == AtMostOneEffect(effects)
\* A deliberately false invariant used only to obtain a non-vacuity witness.
NoLostAckReplay == lost \cap replayed = {}
=============================================================================
