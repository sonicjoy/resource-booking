---- MODULE AdminLocks ----
(***************************************************************************)
(* The row locks taken by booking writes, retirement and deactivation, as  *)
(* the code takes them (design: "Booking, editing and cancelling",          *)
(* "Cascades"). PostgreSQL row-lock modes and their conflicts are modelled  *)
(* directly; a transaction takes its locks one at a time, in its own order, *)
(* reads under them, writes, and releases everything at commit.             *)
(*                                                                         *)
(* Checked: no deadlock; at least one active admin; each booking cancelled  *)
(* at most once; no order created for a room already retired; no confirmed *)
(* booking whose owner is inactive; no confirmed booking on a retired room.*)
(* Six switches turn on mistakes a programmer might make, to show the     *)
(* checker finds each: the old order lock order, deactivation locking only *)
(* its target, cascades without row locks, an order reading its room before*)
(* locking, an order without the caller lock, and retirement without the   *)
(* resource lock.                                                          *)
(***************************************************************************)
EXTENDS Naturals, Sequences, FiniteSets

CONSTANTS LockOnlyTarget, CascadeWithoutLocks, OldOrder, OrderChecksEarly, OrderWithoutCallerLock, RetireWithoutLock

Admins == {"u1", "u2"}
Users == Admins
Rooms == {"r1", "r2"}
Kitchen == "k"
Booked == {"b1", "b2", "b3", "b4"}    \* b3 and b4 are created by transactions below
OwnerOf == [b1 |-> "u1", b2 |-> "u2", b3 |-> "u2", b4 |-> "u1"]
ResourceOf == [b1 |-> "r1", b2 |-> "r1", b3 |-> "k", b4 |-> "r1"]

\* PostgreSQL row-lock conflicts: FOR KEY SHARE (KS), FOR SHARE (S), FOR NO KEY UPDATE (NKU), FOR UPDATE (U).
Conflict(m1, m2) ==
    \/ m1 = "U" \/ m2 = "U"
    \/ (m1 = "NKU" /\ m2 \in {"S", "NKU"})
    \/ (m2 = "NKU" /\ m1 \in {"S", "NKU"})

L(row, mode) == [row |-> row, mode |-> mode]

Txns == {"edit", "order", "book", "retire", "deact1", "deact2"}

\* The locks each transaction takes before it reads, in the code's order.
\*  edit:   BookingService on b1 - resource FOR UPDATE, caller FOR SHARE, booking FOR UPDATE.
\*  order:  a kitchen order by u2 delivered to r1 - kitchen FOR UPDATE, the delivery room FOR KEY SHARE
\*          (taken before the caller, as the insert's foreign key would take it anyway), caller FOR SHARE.
\*          The proposal row is SKIP LOCKED, so it never waits. OldOrder switches to the order before the fix.
\*  retire: ResourceAdmin on r1 - resource FOR UPDATE, then the cascade.
\*  deactN: UserAdmin on uN - the target and every active admin FOR NO KEY UPDATE in id order, then the cascade.
FirstLocks(t) ==
    CASE t = "edit"   -> << L("r1", "U"), L("u1", "S"), L("b1", "U") >>
      [] t = "order"  -> IF OldOrder THEN << L(Kitchen, "U"), L("u2", "S"), L("r1", "KS") >>
                         ELSE IF OrderWithoutCallerLock THEN << L(Kitchen, "U"), L("r1", "KS") >>
                         ELSE << L(Kitchen, "U"), L("r1", "KS"), L("u2", "S") >>
      [] t = "book"   -> << L("r1", "U"), L("u1", "S") >>
      [] t = "retire" -> IF RetireWithoutLock THEN << >> ELSE << L("r1", "U") >>
      [] t = "deact1" -> IF LockOnlyTarget THEN << L("u1", "NKU") >> ELSE << L("u1", "NKU"), L("u2", "NKU") >>
      [] t = "deact2" -> IF LockOnlyTarget THEN << L("u2", "NKU") >> ELSE << L("u1", "NKU"), L("u2", "NKU") >>

Target(t) == IF t = "deact1" THEN "u1" ELSE "u2"

VARIABLES
    phase,      \* t -> "locking" | "cascade" | "done"
    todo,       \* t -> locks still to take, in order
    held,       \* t -> set of locks held
    picked,     \* t -> bookings a cascade picked (ids read before locking them)
    exists,     \* bookings that have been created
    confirmed,  \* bookings that are confirmed
    cancels,    \* b -> number of times cancelled
    active,     \* active users
    retired,    \* retired resources
    seen,       \* what the order read about its room and caller before taking any lock
    badOrder    \* an order was created for a room already retired

vars == << phase, todo, held, picked, exists, confirmed, cancels, active, retired, seen, badOrder >>

Init ==
    /\ phase = [t \in Txns |-> "locking"]
    /\ todo = [t \in Txns |-> FirstLocks(t)]
    /\ held = [t \in Txns |-> {}]
    /\ picked = [t \in Txns |-> {}]
    /\ exists = {"b1", "b2"}
    /\ confirmed = {"b1", "b2"}
    /\ cancels = [b \in Booked |-> 0]
    /\ active = Users
    /\ retired = {}
    /\ seen = [retired |-> {}, active |-> Users]
    /\ badOrder = FALSE

Free(t, lock) == \A o \in Txns \ {t} : \A h \in held[o] : h.row = lock.row => ~Conflict(h.mode, lock.mode)

\* Take the next lock when no other transaction holds a conflicting one.
Take(t) ==
    /\ phase[t] # "done"
    /\ todo[t] # << >>
    /\ Free(t, Head(todo[t]))
    /\ held' = [held EXCEPT ![t] = @ \cup {Head(todo[t])}]
    /\ todo' = [todo EXCEPT ![t] = Tail(@)]
    /\ seen' = IF t = "order" /\ held[t] = {} THEN [retired |-> retired, active |-> active] ELSE seen
    /\ UNCHANGED << phase, picked, exists, confirmed, cancels, active, retired, badOrder >>

Release(t) == held' = [held EXCEPT ![t] = {}]

IdOrder == << "b1", "b2", "b3", "b4" >>
InIdOrder(ids) == SelectSeq(IdOrder, LAMBDA b : b \in ids)
SortedLocks(ids) == [i \in 1..Len(InIdOrder(ids)) |-> L(InIdOrder(ids)[i], "NKU")]

\* A cascade reads the matching confirmed ids without locks, then (unless the mistake is on) locks
\* each in id order and re-checks it.
StartCascade(t, ids) ==
    /\ phase' = [phase EXCEPT ![t] = "cascade"]
    /\ picked' = [picked EXCEPT ![t] = ids]
    /\ todo' = [todo EXCEPT ![t] = IF CascadeWithoutLocks THEN << >>
                                   ELSE IF ids = {} THEN << >>
                                   ELSE SortedLocks(ids)]

CancelPicked(t) ==
    LET gone == IF CascadeWithoutLocks THEN picked[t] ELSE picked[t] \cap confirmed IN
    /\ confirmed' = confirmed \ gone
    /\ cancels' = [b \in Booked |-> IF b \in gone THEN cancels[b] + 1 ELSE cancels[b]]

\* Every lock taken: read under the locks. A transaction's writes become visible to others when it
\* commits (READ COMMITTED), which for retirement and deactivation is after their cascade.
Commit(t) ==
    /\ phase[t] = "locking"
    /\ todo[t] = << >>
    /\ CASE t = "edit" ->
              /\ phase' = [phase EXCEPT ![t] = "done"] /\ Release(t)
              /\ UNCHANGED << todo, picked, exists, confirmed, cancels, active, retired, seen, badOrder >>
         [] t = "order" ->
              \* Refused if the caller is inactive or the delivery room retired; otherwise b3 is created.
              /\ phase' = [phase EXCEPT ![t] = "done"] /\ Release(t)
              \* The code reads the room and the caller under their locks; OrderChecksEarly reads them before.
              /\ LET r == IF OrderChecksEarly THEN seen.retired ELSE retired
                     a == IF OrderChecksEarly THEN seen.active ELSE active IN
                 IF "u2" \in a /\ "r1" \notin r
                    THEN /\ exists' = exists \cup {"b3"} /\ confirmed' = confirmed \cup {"b3"}
                         /\ badOrder' = ("r1" \in retired)
                    ELSE UNCHANGED << exists, confirmed, badOrder >>
              /\ UNCHANGED << todo, picked, cancels, active, retired, seen >>
         [] t = "book" ->
              \* A booking of r1 by u1: refused if r1 is retired or u1 inactive (read under the locks).
              /\ phase' = [phase EXCEPT ![t] = "done"] /\ Release(t)
              /\ IF "u1" \in active /\ "r1" \notin retired
                    THEN /\ exists' = exists \cup {"b4"} /\ confirmed' = confirmed \cup {"b4"}
                    ELSE UNCHANGED << exists, confirmed >>
              /\ UNCHANGED << todo, picked, cancels, active, retired, seen, badOrder >>
         [] t = "retire" ->
              /\ StartCascade(t, {b \in confirmed : ResourceOf[b] = "r1"})
              /\ UNCHANGED << held, exists, confirmed, cancels, active, retired, seen, badOrder >>
         [] t \in {"deact1", "deact2"} ->
              \* LAST_ADMIN: count active admins as read now (under the locks, unless the mistake is on).
              IF Target(t) \in active /\ Cardinality(active \cap Admins) <= 1
                  THEN /\ phase' = [phase EXCEPT ![t] = "done"] /\ Release(t)
                       /\ UNCHANGED << todo, picked, exists, confirmed, cancels, active, retired, seen, badOrder >>
                  ELSE /\ StartCascade(t, {b \in confirmed : OwnerOf[b] = Target(t)})
                       /\ UNCHANGED << held, exists, confirmed, cancels, active, retired, seen, badOrder >>

FinishCascade(t) ==
    /\ phase[t] = "cascade"
    /\ todo[t] = << >>
    /\ CancelPicked(t)
    /\ retired' = IF t = "retire" THEN retired \cup {"r1"} ELSE retired
    /\ active' = IF t \in {"deact1", "deact2"} THEN active \ {Target(t)} ELSE active
    /\ phase' = [phase EXCEPT ![t] = "done"]
    /\ Release(t)
    /\ UNCHANGED << todo, picked, exists, seen, badOrder >>

Done == /\ \A t \in Txns : phase[t] = "done"
        /\ UNCHANGED vars

Next == \/ \E t \in Txns : Take(t) \/ Commit(t) \/ FinishCascade(t)
        \/ Done

Spec == Init /\ [][Next]_vars

AtLeastOneAdmin == active \cap Admins # {}
CancelledOnce == \A b \in Booked : cancels[b] <= 1
NoOrderForARetiredRoom == ~badOrder
NoBookingForAnInactiveOwner == \A b \in confirmed : OwnerOf[b] \in active
NothingOnARetiredRoom == \A b \in confirmed : ResourceOf[b] \notin retired
====
