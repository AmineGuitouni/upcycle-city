using System;

namespace UpcycleCity.Networking
{
    /// <summary>
    /// Host-authoritative shared co-op team wallet. All players contribute coins earned from sorting,
    /// buy-back, bin servicing, and scrubber repairs into one shared balance, and shop purchases deduct
    /// atomically from the same pool so no duplicate spends can occur.
    /// </summary>
    public sealed class SharedTeamWallet
    {
        int balance;

        /// <summary>Current non-negative team coin balance.</summary>
        public int Balance => balance;

        /// <summary>Raised whenever the balance changes: (newBalance, delta).</summary>
        public event Action<int, int> BalanceChanged;

        public SharedTeamWallet(int initialBalance = 0)
        {
            balance = initialBalance < 0 ? 0 : initialBalance;
        }

        /// <summary>Adds coins to the shared team wallet. Ignores non-positive amounts.</summary>
        public int Credit(int amount)
        {
            if (amount <= 0)
                return balance;

            long next = (long)balance + amount;
            int clamped = next > int.MaxValue ? int.MaxValue : (int)next;
            int delta = clamped - balance;
            if (delta == 0)
                return balance;

            balance = clamped;
            BalanceChanged?.Invoke(balance, delta);
            return balance;
        }

        /// <summary>Attempts an atomic deduction for a shop purchase. Returns false without modifying balance if insufficient.</summary>
        public bool TrySpend(int amount)
        {
            if (amount < 0)
                return false;
            if (amount == 0)
                return true;
            if (balance < amount)
                return false;

            balance -= amount;
            BalanceChanged?.Invoke(balance, -amount);
            return true;
        }

        /// <summary>Refunds a previously spent amount (e.g. when the buyer's hotbar was full).</summary>
        public int Refund(int amount) => Credit(amount);

        /// <summary>Sets the wallet balance directly (used when initializing or synchronizing from host state).</summary>
        public void SetBalance(int newBalance)
        {
            int clamped = newBalance < 0 ? 0 : newBalance;
            if (clamped == balance)
                return;

            int delta = clamped - balance;
            balance = clamped;
            BalanceChanged?.Invoke(balance, delta);
        }
    }
}
